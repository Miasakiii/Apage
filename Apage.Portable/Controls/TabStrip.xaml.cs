using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Apage.Portable.Controls
{
    /// <summary>
    /// 标签条（本期静态桩数据版：3 个示例标签 + 占位 favicon）。
    /// 后续接入 Core 的标签会话模型后，桩数据与增删逻辑整体替换。
    /// </summary>
    public partial class TabStrip : UserControl
    {
        private readonly ObservableCollection<TabItemModel> _tabs =
            new ObservableCollection<TabItemModel>();

        public TabStrip()
        {
            InitializeComponent();

            // 静态桩数据：示例标题，favicon 用模板里的占位圆块
            _tabs.Add(new TabItemModel { Title = "新标签页", IsCurrent = true });
            _tabs.Add(new TabItemModel { Title = "Apage 产品设计文档" });
            _tabs.Add(new TabItemModel { Title = "GitHub - 示例仓库" });
            TabItems.ItemsSource = _tabs;
        }

        // 点击标签：切换为当前标签
        private void Tab_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var tab = ((FrameworkElement)sender).DataContext as TabItemModel;
            if (tab == null || tab.IsCurrent) return;
            foreach (var t in _tabs) t.IsCurrent = false;
            tab.IsCurrent = true;
        }

        // 关闭 ✕：本期直接从桩列表移除；若关的是当前标签，把当前让给第一个
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            var tab = ((FrameworkElement)sender).DataContext as TabItemModel;
            if (tab == null) return;
            e.Handled = true; // 不冒泡为“切换标签”
            _tabs.Remove(tab);
            if (tab.IsCurrent && _tabs.Count > 0)
                _tabs[0].IsCurrent = true;
        }

        // + 按钮：本期向桩列表追加一个示例标签
        private void NewTabButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var t in _tabs) t.IsCurrent = false;
            _tabs.Add(new TabItemModel { Title = "新标签页", IsCurrent = true });
        }

        /// <summary>标签桩模型，仅本期使用。</summary>
        internal class TabItemModel : INotifyPropertyChanged
        {
            private string _title;
            private bool _isCurrent;

            public string Title
            {
                get { return _title; }
                set { _title = value; OnPropertyChanged(); }
            }

            public bool IsCurrent
            {
                get { return _isCurrent; }
                set { _isCurrent = value; OnPropertyChanged(); }
            }

            public event PropertyChangedEventHandler PropertyChanged;

            private void OnPropertyChanged([CallerMemberName] string name = null)
            {
                var handler = PropertyChanged;
                if (handler != null) handler(this, new PropertyChangedEventArgs(name));
            }
        }
    }
}
