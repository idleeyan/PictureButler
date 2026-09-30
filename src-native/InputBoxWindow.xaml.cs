using System.Windows;
using System.Windows.Input;

namespace PictureButler;

/// <summary>轻量输入框（重命名等场景）</summary>
public partial class InputBoxWindow : Window
{
    public string Value { get; private set; } = "";

    public InputBoxWindow(string title, string label, string initial = "")
    {
        InitializeComponent();
        TitleText.Text = title;
        LabelText.Text = label;
        InputBox.Text = initial;
        InputBox.SelectAll();
        Loaded += (_, _) => InputBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Value = InputBox.Text;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Ok_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            Cancel_Click(sender, e);
        }
    }
}
