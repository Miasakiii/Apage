using System.Windows;

namespace Apage.Portable
{
    /// <summary>
    /// 主窗口外壳：标签条 + 工具栏 + 内容区（frontend-design.md §5.1）。
    /// 拖窗/双击最大化由 WindowChrome 标题区原生处理，此处只转发工具栏导航命令。
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            BrowserView.GoBack();
        }

        private void ForwardButton_Click(object sender, RoutedEventArgs e)
        {
            BrowserView.GoForward();
        }

        private void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            BrowserView.Reload();
        }
    }
}
