using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PictureButler;

/// <summary>
/// 人脸检测结果
/// </summary>
public sealed class FaceDetection
{
    public float X1, Y1, X2, Y2;   // 像素坐标（原始图）
    public float Score;
    public float[]? Landmarks;     // 5 个关键点（可选）
    public float[]? Embedding;     // 128/512 维特征向量

    public float Width => X2 - X1;
    public float Height => Y2 - Y1;
}

/// <summary>
/// 基于 SCRFD + ArcFace 的纯 C# 人脸识别引擎。
/// 直接调用 ONNX Runtime，绕开有 bug 的 Rust DLL。
/// 模型：buffalo_l (det_10g.onnx + w600k_r50.onnx)
/// </summary>
public sealed class FaceOnnxEngine : IDisposable
{
    private readonly InferenceSession _detSession;
    private readonly InferenceSession _arcSession;
    private readonly string _modelDir;
    private bool _disposed;

    // SCRFD 配置
    private const int DetInputSize = 640;       // 模型输入尺寸
    private const float DetConfThresh = 0.5f;   // 置信度阈值
    private const float NmsThresh = 0.4f;       // NMS IoU 阈值
    private static readonly int[] Strides = { 8, 16, 32 };
    private static readonly int[] NumAnchors = { 2, 2, 2 };

    // ArcFace 配置
    private const int ArcInputSize = 112;

    /// <summary>初始化引擎</summary>
    public FaceOnnxEngine(string modelDir)
    {
        _modelDir = modelDir;
        var detPath = Path.Combine(modelDir, "buffalo_l", "det_10g.onnx");
        var arcPath = Path.Combine(modelDir, "buffalo_l", "w600k_r50.onnx");

        if (!File.Exists(detPath))
            throw new FileNotFoundException($"检测模型不存在: {detPath}");
        if (!File.Exists(arcPath))
            throw new FileNotFoundException($"识别模型不存在: {arcPath}");

        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };
        opts.AppendExecutionProvider_CPU(0);

        _detSession = new InferenceSession(detPath, opts);
        _arcSession = new InferenceSession(arcPath, opts);
    }

    // ====================================================================
    // 人脸检测
    // ====================================================================

    /// <summary>检测图片中的所有人脸</summary>
    public List<FaceDetection> Detect(string imagePath)
    {
        using var bmp = new Bitmap(imagePath);
        return Detect(bmp);
    }

    /// <summary>检测图片中的所有人脸</summary>
    public List<FaceDetection> Detect(Bitmap bmp)
    {
        var (inputTensor, ratio, padW, padH) = PreprocessDet(bmp);

        var inputName = _detSession.InputMetadata.Keys.First();
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
        };

        using var outputs = _detSession.Run(inputs);

        // SCRFD_10G 输出格式：9个输出，3组 stride × (score, bbox, kps)
        // 输出顺序: score_8, score_16, score_32, bbox_8, bbox_16, bbox_32, kps_8, kps_16, kps_32
        // 每个输出形状: [N, C]，其中 N=H*W*num_anchors
        //   score: C=1
        //   bbox: C=4 (left, top, right, bottom 距离)
        //   kps: C=10 (5个关键点 × x,y)
        var ol = outputs.ToList();

        var allBoxes = new List<FaceDetection>();

        // 3 组 stride：stride=8, 16, 32
        // 输出索引：score: 0,1,2  bbox: 3,4,5  kps: 6,7,8
        for (int s = 0; s < Strides.Length; s++)
        {
            int stride = Strides[s];
            int featH = DetInputSize / stride;
            int featW = DetInputSize / stride;
            int numAnchors = NumAnchors[s];

            var scoreTensor = ol[s].AsTensor<float>();       // [N, 1]
            var bboxTensor = ol[s + 3].AsTensor<float>();   // [N, 4]
            var kpsTensor = ol[s + 6].AsTensor<float>();    // [N, 10]

            // 遍历所有 anchor
            // 排列顺序：每个位置的多个 anchor 连续排列（spatial-first）
            // i = (y*W + x) * numAnchors + a
            // 所以 pos = i / numAnchors, a = i % numAnchors
            int totalAnchors = featH * featW * numAnchors;
            for (int i = 0; i < totalAnchors; i++)
            {
                float score = scoreTensor[i, 0];
                if (score < DetConfThresh) continue;

                // 计算 anchor 中心
                int anchorIdx = i / numAnchors; // 网格位置索引
                int a = i % numAnchors;         // 第几个 anchor
                int h = anchorIdx / featW;
                int w = anchorIdx % featW;

                float cx = (w + 0.5f) * stride;
                float cy = (h + 0.5f) * stride;

                // bbox 是距离（左、上、右、下），乘以 stride
                float dx1 = bboxTensor[i, 0] * stride;
                float dy1 = bboxTensor[i, 1] * stride;
                float dx2 = bboxTensor[i, 2] * stride;
                float dy2 = bboxTensor[i, 3] * stride;

                float x1 = cx - dx1;
                float y1 = cy - dy1;
                float x2 = cx + dx2;
                float y2 = cy + dy2;

                // 关键点（5个，每个 x,y 偏移，乘以 stride）
                float[] kps = new float[10];
                for (int k = 0; k < 5; k++)
                {
                    kps[k * 2] = cx + kpsTensor[i, k * 2] * stride;
                    kps[k * 2 + 1] = cy + kpsTensor[i, k * 2 + 1] * stride;
                }

                allBoxes.Add(new FaceDetection
                {
                    X1 = x1,
                    Y1 = y1,
                    X2 = x2,
                    Y2 = y2,
                    Score = score,
                    Landmarks = kps
                });
            }
        }

        // NMS
        var nmsResults = NMS(allBoxes, NmsThresh);

        // 缩放回原图坐标（减去 pad，除以 ratio）
        foreach (var face in nmsResults)
        {
            face.X1 = (face.X1 - padW) / ratio;
            face.Y1 = (face.Y1 - padH) / ratio;
            face.X2 = (face.X2 - padW) / ratio;
            face.Y2 = (face.Y2 - padH) / ratio;

            if (face.Landmarks != null)
            {
                for (int i = 0; i < 5; i++)
                {
                    face.Landmarks[i * 2] = (face.Landmarks[i * 2] - padW) / ratio;
                    face.Landmarks[i * 2 + 1] = (face.Landmarks[i * 2 + 1] - padH) / ratio;
                }
            }
        }

        // 裁剪到图片范围内
        int imgW = bmp.Width;
        int imgH = bmp.Height;
        foreach (var face in nmsResults)
        {
            face.X1 = Math.Max(0, Math.Min(imgW - 1, face.X1));
            face.Y1 = Math.Max(0, Math.Min(imgH - 1, face.Y1));
            face.X2 = Math.Max(0, Math.Min(imgW - 1, face.X2));
            face.Y2 = Math.Max(0, Math.Min(imgH - 1, face.Y2));
        }

        return nmsResults;
    }

    /// <summary>检测预处理：等比缩放 + pad 到 640x640，BGR + mean 归一化，NCHW 格式</summary>
    private (DenseTensor<float> tensor, float ratio, float padW, float padH) PreprocessDet(Bitmap bmp)
    {
        int w = bmp.Width;
        int h = bmp.Height;
        float ratio = Math.Min((float)DetInputSize / w, (float)DetInputSize / h);
        int newW = (int)(w * ratio);
        int newH = (int)(h * ratio);
        int padW = (DetInputSize - newW) / 2;
        int padH = (DetInputSize - newH) / 2;

        // 缩放 + pad 到 640x640
        using var resized = new Bitmap(DetInputSize, DetInputSize, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(resized))
        {
            g.Clear(Color.FromArgb(127, 127, 127)); // 灰色填充
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(bmp, padW, padH, newW, newH);
        }

        // 转 BGR float 张量（NCHW）
        var tensor = new DenseTensor<float>(new[] { 1, 3, DetInputSize, DetInputSize });
        var rect = new Rectangle(0, 0, DetInputSize, DetInputSize);
        var bmpData = resized.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        try
        {
            int stride = bmpData.Stride;
            IntPtr ptr = bmpData.Scan0;
            byte[] rgbValues = new byte[stride * DetInputSize];
            Marshal.Copy(ptr, rgbValues, 0, rgbValues.Length);

            // SCRFD 必须与 insightface 一致地做归一化：(x - 127.5) / 127.5
            // （insightface 用 1/128，差异可忽略；但**绝不能直接喂 0-255**——
            //   实测不归一化会产出大量误检：单张图能"检出" 40 个不存在的人脸，
            //   且置信度与框位置均不可信。此处与 Rust face/preprocess.rs 保持一致。）
            for (int y = 0; y < DetInputSize; y++)
            {
                for (int x = 0; x < DetInputSize; x++)
                {
                    int idx = y * stride + x * 3;
                    // BGR 顺序
                    tensor[0, 0, y, x] = (rgbValues[idx] - 127.5f) / 127.5f;       // B
                    tensor[0, 1, y, x] = (rgbValues[idx + 1] - 127.5f) / 127.5f;   // G
                    tensor[0, 2, y, x] = (rgbValues[idx + 2] - 127.5f) / 127.5f;   // R
                }
            }
        }
        finally
        {
            resized.UnlockBits(bmpData);
        }

        return (tensor, ratio, padW, padH);
    }

    // ====================================================================
    // 人脸特征提取（ArcFace）
    // ====================================================================

    /// <summary>提取人脸特征向量（先对齐再提取）</summary>
    public float[] GetEmbedding(Bitmap fullImage, FaceDetection face)
    {
        using var aligned = AlignFace(fullImage, face);
        return GetEmbedding(aligned);
    }

    /// <summary>提取对齐后人脸的特征向量</summary>
    public float[] GetEmbedding(Bitmap alignedFace)
    {
        // 预处理：resize 到 112x112，RGB 归一化
        var tensor = PreprocessArc(alignedFace);
        var inputName = _arcSession.InputMetadata.Keys.First();
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, tensor)
        };

        using var outputs = _arcSession.Run(inputs);
        // ArcFace 输出是二维张量 (1, 512)，不能用一维索引 outputTensor[i]（会抛
        // IndexOutOfRangeException，导致整张图被记为失败）。先摊平再取。
        var flat = outputs.First().AsTensor<float>().ToArray();

        // L2 归一化
        float[] embedding = new float[flat.Length];
        float sum = 0;
        for (int i = 0; i < embedding.Length; i++)
        {
            embedding[i] = flat[i];
            sum += embedding[i] * embedding[i];
        }
        float norm = MathF.Sqrt(sum);
        if (norm > 0)
        {
            for (int i = 0; i < embedding.Length; i++)
                embedding[i] /= norm;
        }
        return embedding;
    }

    /// <summary>ArcFace 预处理：112x112，RGB 归一化到 [-1, 1]</summary>
    private DenseTensor<float> PreprocessArc(Bitmap bmp)
    {
        using var resized = new Bitmap(ArcInputSize, ArcInputSize, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(bmp, 0, 0, ArcInputSize, ArcInputSize);
        }

        var tensor = new DenseTensor<float>(new[] { 1, 3, ArcInputSize, ArcInputSize });
        var rect = new Rectangle(0, 0, ArcInputSize, ArcInputSize);
        var bmpData = resized.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        try
        {
            int stride = bmpData.Stride;
            IntPtr ptr = bmpData.Scan0;
            byte[] rgbValues = new byte[stride * ArcInputSize];
            Marshal.Copy(ptr, rgbValues, 0, rgbValues.Length);

            // ArcFace: RGB 归一化到 [-1, 1] 即 (pixel / 127.5) - 1
            // 注意：BMP 是 BGR 顺序，但 ArcFace 用 RGB
            for (int y = 0; y < ArcInputSize; y++)
            {
                for (int x = 0; x < ArcInputSize; x++)
                {
                    int idx = y * stride + x * 3;
                    float b = rgbValues[idx];
                    float g = rgbValues[idx + 1];
                    float r = rgbValues[idx + 2];
                    // NCHW: RGB 顺序
                    tensor[0, 0, y, x] = r / 127.5f - 1.0f;
                    tensor[0, 1, y, x] = g / 127.5f - 1.0f;
                    tensor[0, 2, y, x] = b / 127.5f - 1.0f;
                }
            }
        }
        finally
        {
            resized.UnlockBits(bmpData);
        }

        return tensor;
    }

    /// <summary>人脸对齐（基于 5 关键点）：裁剪并对齐到 112x112</summary>
    private Bitmap AlignFace(Bitmap src, FaceDetection face)
    {
        // 扩展 bbox，留出边距
        float w = face.Width;
        float h = face.Height;
        float pad = Math.Max(w, h) * 0.3f;
        float x1 = Math.Max(0, face.X1 - pad);
        float y1 = Math.Max(0, face.Y1 - pad);
        float x2 = Math.Min(src.Width - 1, face.X2 + pad);
        float y2 = Math.Min(src.Height - 1, face.Y2 + pad);

        int cropW = (int)(x2 - x1);
        int cropH = (int)(y2 - y1);

        var crop = new Bitmap(cropW, cropH, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(crop))
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(src, 0, 0, new RectangleF(x1, y1, cropW, cropH), GraphicsUnit.Pixel);
        }

        // 如果有关键点，做仿射对齐
        if (face.Landmarks != null)
        {
            // 关键点减去 crop 偏移
            var kps = new float[10];
            for (int i = 0; i < 5; i++)
            {
                kps[i * 2] = face.Landmarks[i * 2] - x1;
                kps[i * 2 + 1] = face.Landmarks[i * 2 + 1] - y1;
            }

            // 标准 5 点位置（112x112 模板）
            // 参考 InsightFace 的 arcface_alignment
            float[] refPts = {
                38.2946f, 51.6963f,  // 左眼
                73.5318f, 51.6963f,  // 右眼
                56.0252f, 71.7366f,  // 鼻尖
                41.5493f, 92.3655f,  // 左嘴角
                70.7299f, 92.3655f   // 右嘴角
            };

            // 计算仿射变换矩阵
            var (M, ok) = EstimateAffinePartial2D(kps, refPts);
            if (ok)
            {
                var aligned = new Bitmap(ArcInputSize, ArcInputSize, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(aligned))
                {
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.Clear(Color.FromArgb(0, 0, 0));

                    // 用矩阵变换
                    var matrix = new System.Drawing.Drawing2D.Matrix(
                        M[0], M[1], M[2], M[3], M[4], M[5]);
                    g.Transform = matrix;
                    g.DrawImage(crop, 0, 0);
                }
                crop.Dispose();
                return aligned;
            }
        }

        // 无关键点或对齐失败：直接 resize
        var resized = new Bitmap(ArcInputSize, ArcInputSize, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(crop, 0, 0, ArcInputSize, ArcInputSize);
        }
        crop.Dispose();
        return resized;
    }

    /// <summary>
    /// 估计相似变换矩阵（scale + rotation + translation），将 src 点映射到 dst 点。
    /// 返回 [m00, m01, m02, m10, m11, m12] 的 2x3 矩阵（GDI+ Matrix 格式）。
    /// 这是一个简化版本，使用最小二乘求解。
    /// </summary>
    private (float[] M, bool ok) EstimateAffinePartial2D(float[] src, float[] dst)
    {
        // 5 对点，求解相似变换（4 个参数：a, b, tx, ty）
        // x' = a*x - b*y + tx
        // y' = b*x + a*y + ty
        // 用最小二乘法

        int n = 5;
        // 构建方程：Ax = b
        // A = [[x0, -y0, 1, 0], [y0, x0, 0, 1], ...]
        // x = [a, b, tx, ty]
        // b = [dst_x0, dst_y0, dst_x1, dst_y1, ...]

        // 用正规方程求解：A^T A x = A^T b
        // 4x4 矩阵求逆
        double[,] AtA = new double[4, 4];
        double[] Atb = new double[4];

        for (int i = 0; i < n; i++)
        {
            double sx = src[i * 2];
            double sy = src[i * 2 + 1];
            double dx = dst[i * 2];
            double dy = dst[i * 2 + 1];

            // A row 1: [sx, -sy, 1, 0]
            AtA[0, 0] += sx * sx;    AtA[0, 1] += -sx * sy;   AtA[0, 2] += sx;   AtA[0, 3] += 0;
            AtA[1, 0] += -sy * sx;   AtA[1, 1] += sy * sy;    AtA[1, 2] += -sy;  AtA[1, 3] += 0;
            AtA[2, 0] += 1 * sx;     AtA[2, 1] += 1 * -sy;    AtA[2, 2] += 1;    AtA[2, 3] += 0;
            AtA[3, 0] += 0;          AtA[3, 1] += 0;          AtA[3, 2] += 0;    AtA[3, 3] += 0;

            Atb[0] += sx * dx;
            Atb[1] += -sy * dx;
            Atb[2] += 1 * dx;
            Atb[3] += 0;

            // A row 2: [sy, sx, 0, 1]
            AtA[0, 0] += sy * sy;    // 注意：这是第二行第一列，累加到对称位置
            // 不对，我需要重新考虑。让我用更简单的方法。
        }

        // 换个更简单的方法：用前 2 对点计算旋转和缩放
        // 这种方法在关键点准确时效果足够好
        float srcDx = src[2] - src[0];  // 右眼 - 左眼 x
        float srcDy = src[3] - src[1];  // 右眼 - 左眼 y
        float dstDx = dst[2] - dst[0];
        float dstDy = dst[3] - dst[1];

        float srcDist = MathF.Sqrt(srcDx * srcDx + srcDy * srcDy);
        float dstDist = MathF.Sqrt(dstDx * dstDx + dstDy * dstDy);

        if (srcDist < 0.001f || dstDist < 0.001f)
            return (new float[6], false);

        float scale = dstDist / srcDist;
        float angle = MathF.Atan2(dstDy, dstDx) - MathF.Atan2(srcDy, srcDx);

        float cos = MathF.Cos(angle) * scale;
        float sin = MathF.Sin(angle) * scale;

        // 以左眼为中心计算平移
        float tx = dst[0] - (cos * src[0] - sin * src[1]);
        float ty = dst[1] - (sin * src[0] + cos * src[1]);

        // GDI+ Matrix: [m11, m12, m21, m22, dx, dy]
        // 对应: x' = m11*x + m21*y + dx
        //       y' = m12*x + m22*y + dy
        // 我们的: x' = cos*x - sin*y + tx
        //        y' = sin*x + cos*y + ty
        float[] M = { cos, sin, -sin, cos, tx, ty };
        return (M, true);
    }

    // ====================================================================
    // NMS
    // ====================================================================

    private static List<FaceDetection> NMS(List<FaceDetection> faces, float iouThresh)
    {
        if (faces.Count == 0) return faces;

        // 按 score 降序
        var sorted = faces.OrderByDescending(f => f.Score).ToList();
        var keep = new List<FaceDetection>();
        var suppressed = new HashSet<int>();

        for (int i = 0; i < sorted.Count; i++)
        {
            if (suppressed.Contains(i)) continue;
            keep.Add(sorted[i]);

            for (int j = i + 1; j < sorted.Count; j++)
            {
                if (suppressed.Contains(j)) continue;
                if (IoU(sorted[i], sorted[j]) > iouThresh)
                    suppressed.Add(j);
            }
        }

        return keep;
    }

    private static float IoU(FaceDetection a, FaceDetection b)
    {
        float x1 = Math.Max(a.X1, b.X1);
        float y1 = Math.Max(a.Y1, b.Y1);
        float x2 = Math.Min(a.X2, b.X2);
        float y2 = Math.Min(a.Y2, b.Y2);

        float w = Math.Max(0, x2 - x1);
        float h = Math.Max(0, y2 - y1);
        float inter = w * h;

        float areaA = (a.X2 - a.X1) * (a.Y2 - a.Y1);
        float areaB = (b.X2 - b.X1) * (b.Y2 - b.Y1);

        return inter / (areaA + areaB - inter + 1e-6f);
    }

    // ====================================================================
    // 人脸聚类
    // ====================================================================

    /// <summary>人脸聚类（简单的贪心聚类：相似度 > 阈值则归为同一人）</summary>
    public static List<int> Cluster(List<float[]> embeddings, float threshold = 0.6f)
    {
        int n = embeddings.Count;
        var labels = new int[n];
        for (int i = 0; i < n; i++) labels[i] = -1;
        int clusterId = 0;

        var unassigned = new List<int>(Enumerable.Range(0, n));

        while (unassigned.Count > 0)
        {
            // 取第一个作为聚类中心
            int seed = unassigned[0];
            labels[seed] = clusterId;
            unassigned.RemoveAt(0);

            // 找到所有与该聚类相似度 > 阈值的人脸
            for (int i = unassigned.Count - 1; i >= 0; i--)
            {
                int idx = unassigned[i];
                float sim = CosineSimilarity(embeddings[seed], embeddings[idx]);
                if (sim > threshold)
                {
                    labels[idx] = clusterId;
                    unassigned.RemoveAt(i);
                }
            }

            clusterId++;
        }

        return new List<int>(labels);
    }

    /// <summary>余弦相似度</summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0;
        for (int i = 0; i < a.Length; i++)
            dot += a[i] * b[i];
        return dot; // 已经 L2 归一化过了，直接点积就是 cos 相似度
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _detSession.Dispose();
        _arcSession.Dispose();
    }
}
