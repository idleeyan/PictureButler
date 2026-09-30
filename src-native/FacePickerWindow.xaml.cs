using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace PictureButler;

/// <summary>选择一张人脸作为人物封面</summary>
public partial class FacePickerWindow : Window
{
    public long SelectedFaceId { get; private set; }

    public FacePickerWindow(string personName, List<PersonFaceItem> faces)
    {
        InitializeComponent();
        TitleText.Text = $"更换封面 · {personName}";
        FaceList.ItemsSource = faces;
        if (faces.Count == 0)
        {
            HintText.Text = "　该人物暂无人脸可作封面，请先对相关图片进行人脸识别";
        }
    }

    private void Face_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PersonFaceItem f)
        {
            SelectedFaceId = f.FaceId;
            DialogResult = true;
            Close();
        }
    }

    /// <summary>自绘标题栏拖动（无边框窗口）</summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
