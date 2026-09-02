using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Web.Script.Serialization;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;

namespace StickyMemo
{
    public enum NoteKind { Todo, Idea }

    public sealed class Note
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Content { get; set; }
        public NoteKind Kind { get; set; }
        public DateTime UpdatedAt { get; set; }

        public Note()
        {
            Id = Guid.NewGuid().ToString("N");
            Title = "未命名";
            Content = "";
            UpdatedAt = DateTime.Now;
        }
    }

    public sealed class AppData
    {
        public List<Note> Notes { get; set; }
        public string SelectedId { get; set; }
        public bool Topmost { get; set; }
        public bool PinnedOpen { get; set; }
        public double ExpandedWidth { get; set; }
        public double ExpandedHeight { get; set; }

        public AppData()
        {
            Notes = new List<Note>();
            Topmost = true;
            PinnedOpen = false;
            ExpandedWidth = 860;
            ExpandedHeight = 590;
        }
    }

    internal sealed class NoteListItem
    {
        public Note Note { get; private set; }
        public NoteListItem(Note note) { Note = note; }
        public override string ToString()
        {
            string icon = Note.Kind == NoteKind.Todo ? "☐" : "✦";
            string title = string.IsNullOrWhiteSpace(Note.Title) ? "未命名" : Note.Title.Trim();
            return icon + "  " + title;
        }
    }

    public sealed class MainWindow : Window
    {
        private static readonly Color Ink = Color.FromRgb(48, 49, 47);
        private static readonly Color Muted = Color.FromRgb(122, 123, 119);
        private static readonly Color Paper = Color.FromRgb(247, 246, 242);
        private static readonly Color PaperDeep = Color.FromRgb(232, 230, 223);
        private static readonly Color Surface = Color.FromRgb(253, 252, 249);
        private static readonly Color Sidebar = Color.FromRgb(240, 239, 234);
        private static readonly Color Accent = Color.FromRgb(103, 126, 107);
        private static readonly Color AccentSoft = Color.FromRgb(226, 234, 226);
        private static readonly Color IdeaAccent = Color.FromRgb(177, 133, 96);
        private static readonly Color Danger = Color.FromRgb(177, 91, 82);
        private static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI");

        private readonly string dataDirectory;
        private readonly string dataPath;
        private AppData appData;
        private Note currentNote;
        private bool isCollapsed;
        private bool isLoadingNote;
        private bool reallyExit;
        private double expandedWidth;
        private double expandedHeight;

        private Grid expandedView;
        private Border collapsedView;
        private TextBox titleBox;
        private TextBox editor;
        private FlowDocumentScrollViewer preview;
        private ListBox noteList;
        private TextBox searchBox;
        private TextBlock searchHint;
        private ComboBox filterBox;
        private TextBlock saveStatus;
        private TextBlock countStatus;
        private TextBlock kindStatus;
        private Button pinButton;
        private DispatcherTimer saveTimer;
        private DispatcherTimer collapseTimer;
        private Forms.NotifyIcon trayIcon;
        private Forms.ToolStripMenuItem trayTopmostItem;
        private Drawing.Icon notebookIcon;

        public MainWindow()
        {
            dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StickyMemo");
            dataPath = Path.Combine(dataDirectory, "notes.json");
            appData = LoadData();
            EnsureFirstNote();
            expandedWidth = Math.Max(720, appData.ExpandedWidth);
            expandedHeight = Math.Max(480, appData.ExpandedHeight);

            Title = "栖笺 PerchNote";
            Width = expandedWidth;
            Height = expandedHeight;
            MinWidth = 720;
            MinHeight = 480;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = true;
            Topmost = appData.Topmost;
            FontFamily = UiFont;
            FontSize = 13.5;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

            BuildViews();
            BuildTimers();
            BuildTrayIcon();
            RegisterEvents();
            isCollapsed = false;
            Content = expandedView;

            Loaded += delegate
            {
                PositionAtRightEdge();
                SelectInitialNote();
                editor.Focus();
            };
        }

        private AppData LoadData()
        {
            try
            {
                if (!File.Exists(dataPath)) return new AppData();
                string json = File.ReadAllText(dataPath, Encoding.UTF8);
                AppData loaded = new JavaScriptSerializer().Deserialize<AppData>(json);
                return loaded ?? new AppData();
            }
            catch
            {
                try
                {
                    if (File.Exists(dataPath)) File.Copy(dataPath, dataPath + ".broken", true);
                }
                catch { }
                return new AppData();
            }
        }

        private void EnsureFirstNote()
        {
            if (appData.Notes == null) appData.Notes = new List<Note>();
            if (appData.Notes.Count == 0)
            {
                Note welcome = new Note();
                welcome.Title = "欢迎使用栖笺";
                welcome.Kind = NoteKind.Idea;
                welcome.Content = "# 欢迎使用栖笺\n\n在左边输入，右边会**实时预览**。\n\n## 今天可以做什么？\n\n- [ ] 写下第一件待办\n- [x] 打开栖笺\n\n> 灵感稍纵即逝，先记下来再说。\n\n支持 `Markdown`、列表、链接和代码块。";
                welcome.UpdatedAt = DateTime.Now;
                appData.Notes.Add(welcome);
                appData.SelectedId = welcome.Id;
            }
        }

        private void BuildTimers()
        {
            saveTimer = new DispatcherTimer();
            saveTimer.Interval = TimeSpan.FromMilliseconds(650);
            saveTimer.Tick += delegate { saveTimer.Stop(); SaveNow(); };

            collapseTimer = new DispatcherTimer();
            collapseTimer.Interval = TimeSpan.FromMilliseconds(900);
            collapseTimer.Tick += delegate
            {
                collapseTimer.Stop();
                if (!appData.PinnedOpen && !IsMouseOver) ShowCollapsed();
            };
        }

        private void BuildViews()
        {
            expandedView = CreateExpandedView();
            collapsedView = CreateCollapsedView();
        }

        private Grid CreateExpandedView()
        {
            Grid shell = new Grid();
            Border card = new Border();
            card.CornerRadius = new CornerRadius(20);
            card.Background = new SolidColorBrush(Paper);
            card.BorderBrush = new SolidColorBrush(PaperDeep);
            card.BorderThickness = new Thickness(1);
            card.Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 8, Opacity = 0.18, Color = Color.FromRgb(38, 40, 37) };
            shell.Children.Add(card);

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(66) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
            card.Child = root;

            UIElement header = CreateHeader();
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            UIElement workspace = CreateWorkspace();
            Grid.SetRow(workspace, 1);
            root.Children.Add(workspace);

            UIElement status = CreateStatusBar();
            Grid.SetRow(status, 2);
            root.Children.Add(status);
            return shell;
        }

        private UIElement CreateHeader()
        {
            Grid header = new Grid { Margin = new Thickness(18, 11, 12, 9), Background = Brushes.Transparent };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Border logo = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Accent) };
            logo.Child = new TextBlock { Text = "✦", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            header.Children.Add(logo);

            titleBox = new TextBox
            {
                FontFamily = UiFont,
                FontSize = 18.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Ink),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 18, 0),
                CaretBrush = new SolidColorBrush(Accent),
                ToolTip = "便签标题"
            };
            Grid.SetColumn(titleBox, 1);
            header.Children.Add(titleBox);

            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Button todo = MakeHeaderButton("＋  待办", "新建待办 (Ctrl+Shift+N)");
            todo.Click += delegate { CreateNote(NoteKind.Todo); };
            actions.Children.Add(todo);
            Button idea = MakeHeaderButton("✦  灵感", "新建灵感 (Ctrl+N)");
            idea.Click += delegate { CreateNote(NoteKind.Idea); };
            actions.Children.Add(idea);
            pinButton = MakeHeaderButton(appData.PinnedOpen ? "●  固定" : "○  固定", "固定展开，不再自动缩略");
            pinButton.Click += TogglePinned;
            actions.Children.Add(pinButton);
            Border divider = new Border { Width = 1, Height = 20, Background = new SolidColorBrush(PaperDeep), Margin = new Thickness(7, 0, 5, 0) };
            actions.Children.Add(divider);
            Button collapse = MakeSquareButton("−", "缩略到屏幕边缘 (Ctrl+M)");
            collapse.Click += delegate { ShowCollapsed(); };
            actions.Children.Add(collapse);
            Button close = MakeSquareButton("×", "隐藏到系统托盘");
            close.Click += delegate { SaveNow(); Hide(); };
            actions.Children.Add(close);
            Grid.SetColumn(actions, 2);
            header.Children.Add(actions);

            header.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.OriginalSource == header && e.ButtonState == MouseButtonState.Pressed) DragMove();
            };
            return header;
        }

        private UIElement CreateWorkspace()
        {
            Grid area = new Grid { Margin = new Thickness(14, 0, 14, 0) };
            area.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            area.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            area.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border sidebar = new Border { Background = new SolidColorBrush(Sidebar), BorderBrush = new SolidColorBrush(PaperDeep), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(10) };
            Grid side = new Grid();
            side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
            sidebar.Child = side;

            Grid searchWrap = new Grid();
            searchBox = new TextBox { Height = 36, Padding = new Thickness(30, 7, 9, 6), FontFamily = UiFont, FontSize = 12.5, BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(PaperDeep), Background = new SolidColorBrush(Surface), Foreground = new SolidColorBrush(Ink), CaretBrush = new SolidColorBrush(Accent), ToolTip = "搜索标题或内容" };
            searchHint = new TextBlock { Text = "⌕   搜索便签", FontFamily = UiFont, FontSize = 12.5, Foreground = new SolidColorBrush(Muted), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            searchWrap.Children.Add(searchBox);
            searchWrap.Children.Add(searchHint);
            side.Children.Add(searchWrap);
            filterBox = new ComboBox { Height = 32, Margin = new Thickness(1, 8, 1, 8), Padding = new Thickness(5, 2, 5, 2), FontFamily = UiFont, FontSize = 12.5, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = new SolidColorBrush(Muted) };
            filterBox.Items.Add("全部便签");
            filterBox.Items.Add("待办事项");
            filterBox.Items.Add("灵感记录");
            filterBox.SelectedIndex = 0;
            Grid.SetRow(filterBox, 1);
            side.Children.Add(filterBox);
            noteList = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontFamily = UiFont, FontSize = 13.5, Foreground = new SolidColorBrush(Ink), Padding = new Thickness(0), ItemContainerStyle = MakeListItemStyle() };
            ContextMenu noteMenu = new ContextMenu();
            MenuItem deleteMenuItem = new MenuItem { Header = "删除这条便签" };
            deleteMenuItem.Click += delegate { DeleteCurrentNote(); };
            noteMenu.Items.Add(deleteMenuItem);
            noteList.ContextMenu = noteMenu;
            Grid.SetRow(noteList, 2);
            side.Children.Add(noteList);

            Grid sideFooter = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            sideFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sideFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Button quickNew = MakeSidebarButton("＋  新建", false);
            quickNew.ToolTip = "新建灵感 (Ctrl+N)";
            quickNew.Click += delegate { CreateNote(NoteKind.Idea); };
            sideFooter.Children.Add(quickNew);
            Button deleteNote = MakeSidebarButton("删除", true);
            deleteNote.ToolTip = "删除当前便签 (Ctrl+Shift+Delete)";
            deleteNote.Click += delegate { DeleteCurrentNote(); };
            Grid.SetColumn(deleteNote, 1);
            sideFooter.Children.Add(deleteNote);
            Grid.SetRow(sideFooter, 3);
            side.Children.Add(sideFooter);
            area.Children.Add(sidebar);

            Grid panes = new Grid();
            panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(panes, 2);
            area.Children.Add(panes);

            Border editorCard = MakePane("MARKDOWN");
            editor = new TextBox
            {
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Ink),
                FontFamily = new FontFamily("Cascadia Mono, Microsoft YaHei UI, Consolas"),
                FontSize = 14,
                CaretBrush = new SolidColorBrush(Accent),
                Padding = new Thickness(14, 12, 14, 14),
                SpellCheck = { IsEnabled = false }
            };
            ((Grid)editorCard.Child).Children.Add(editor);
            Grid.SetRow(editor, 1);
            panes.Children.Add(editorCard);

            Border previewCard = MakePane("实时预览");
            preview = new FlowDocumentScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.Transparent, Padding = new Thickness(4), IsToolBarVisible = false, Zoom = 100 };
            preview.Document = MarkdownRenderer.Render("");
            ((Grid)previewCard.Child).Children.Add(preview);
            Grid.SetRow(preview, 1);
            Grid.SetColumn(previewCard, 2);
            panes.Children.Add(previewCard);
            return area;
        }

        private Border MakePane(string label)
        {
            Border border = new Border { Background = new SolidColorBrush(Surface), BorderBrush = new SolidColorBrush(PaperDeep), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), ClipToBounds = true };
            Grid grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Border bar = new Border { Background = new SolidColorBrush(Color.FromRgb(248, 247, 243)), BorderBrush = new SolidColorBrush(PaperDeep), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(14, 9, 0, 0) };
            bar.Child = new TextBlock { Text = label, FontFamily = UiFont, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Muted) };
            grid.Children.Add(bar);
            border.Child = grid;
            return border;
        }

        private UIElement CreateStatusBar()
        {
            Grid status = new Grid { Margin = new Thickness(22, 0, 20, 0) };
            status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            kindStatus = new TextBlock { Text = "●  灵感", FontFamily = UiFont, FontSize = 11, Foreground = new SolidColorBrush(IdeaAccent), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            saveStatus = new TextBlock { Text = "已保存", FontFamily = UiFont, FontSize = 11, Foreground = new SolidColorBrush(Muted), VerticalAlignment = VerticalAlignment.Center };
            countStatus = new TextBlock { FontFamily = UiFont, FontSize = 11, Foreground = new SolidColorBrush(Muted), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(saveStatus, 1);
            Grid.SetColumn(countStatus, 2);
            status.Children.Add(kindStatus);
            status.Children.Add(saveStatus);
            status.Children.Add(countStatus);
            return status;
        }

        private Border CreateCollapsedView()
        {
            Border tab = new Border
            {
                Width = 34,
                Height = 132,
                CornerRadius = new CornerRadius(12, 0, 0, 12),
                Background = new SolidColorBrush(Accent),
                BorderBrush = new SolidColorBrush(Color.FromRgb(88, 110, 92)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.2, Color = Color.FromRgb(35, 42, 36) }
            };
            StackPanel content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new TextBlock { Text = "•\n•\n•", FontSize = 9, Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, LineHeight = 6, Margin = new Thickness(0, 0, 0, 10) });
            content.Children.Add(new TextBlock { Text = "灵\n笺", FontFamily = UiFont, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextAlignment = TextAlignment.Center, LineHeight = 22 });
            content.Children.Add(new TextBlock { Text = "✦", FontSize = 10, Foreground = new SolidColorBrush(Color.FromArgb(205, 255, 255, 255)), Margin = new Thickness(0, 9, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
            tab.Child = content;
            tab.MouseEnter += delegate { tab.Background = new SolidColorBrush(Color.FromRgb(121, 146, 125)); };
            tab.MouseLeave += delegate { tab.Background = new SolidColorBrush(Accent); };
            tab.MouseLeftButtonUp += delegate { ShowExpanded(); };
            return tab;
        }

        private Button MakeHeaderButton(string text, string tooltip)
        {
            Button button = new Button { Content = text, Height = 32, MinWidth = 55, Margin = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 0, 10, 1), BorderThickness = new Thickness(0), Background = new SolidColorBrush(AccentSoft), Foreground = new SolidColorBrush(Color.FromRgb(69, 84, 71)), FontFamily = UiFont, FontWeight = FontWeights.Medium, FontSize = 11.5, Cursor = Cursors.Hand, ToolTip = tooltip, Template = MakeRoundedButtonTemplate(9, Color.FromRgb(214, 225, 215), Color.FromRgb(203, 217, 205)) };
            return button;
        }

        private Button MakeSquareButton(string text, string tooltip)
        {
            Button button = MakeHeaderButton(text, tooltip);
            button.MinWidth = 32;
            button.Width = 32;
            button.Background = Brushes.Transparent;
            button.Foreground = new SolidColorBrush(Muted);
            button.FontSize = 16;
            button.Padding = new Thickness(0, 0, 0, 2);
            button.Template = MakeRoundedButtonTemplate(9, Color.FromRgb(234, 233, 228), Color.FromRgb(224, 223, 217));
            return button;
        }

        private Button MakeSidebarButton(string text, bool destructive)
        {
            Color hover = destructive ? Color.FromRgb(244, 229, 226) : Color.FromRgb(226, 234, 226);
            Color pressed = destructive ? Color.FromRgb(238, 215, 211) : Color.FromRgb(214, 225, 215);
            Button button = new Button
            {
                Content = text,
                Height = 32,
                MinWidth = destructive ? 48 : 78,
                Padding = new Thickness(10, 0, 10, 1),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(destructive ? Danger : Accent),
                FontFamily = UiFont,
                FontWeight = FontWeights.Medium,
                FontSize = 11.5,
                Cursor = Cursors.Hand,
                Template = MakeRoundedButtonTemplate(8, hover, pressed)
            };
            return button;
        }

        private ControlTemplate MakeRoundedButtonTemplate(double radius, Color hover, Color pressed)
        {
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "ButtonBorder";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            border.AppendChild(presenter);
            template.VisualTree = border;
            Trigger over = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(hover)));
            template.Triggers.Add(over);
            Trigger down = new Trigger { Property = Button.IsPressedProperty, Value = true };
            down.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(pressed)));
            template.Triggers.Add(down);
            return template;
        }

        private Style MakeListItemStyle()
        {
            Style style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 8, 8, 8)));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 1)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.TemplateProperty, MakeListItemTemplate()));
            Trigger selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(AccentSoft)));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(57, 75, 61))));
            style.Triggers.Add(selected);
            Trigger hover = new Trigger { Property = ListBoxItem.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(233, 234, 229))));
            style.Triggers.Add(hover);
            return style;
        }

        private ControlTemplate MakeListItemTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(ListBoxItem));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;
            return template;
        }

        private void RegisterEvents()
        {
            titleBox.TextChanged += OnNoteEdited;
            editor.TextChanged += OnNoteEdited;
            noteList.SelectionChanged += OnNoteSelectionChanged;
            searchBox.TextChanged += delegate
            {
                searchHint.Visibility = string.IsNullOrEmpty(searchBox.Text) && !searchBox.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Collapsed;
                RefreshNoteList();
            };
            searchBox.GotKeyboardFocus += delegate { searchHint.Visibility = Visibility.Collapsed; };
            searchBox.LostKeyboardFocus += delegate { searchHint.Visibility = string.IsNullOrEmpty(searchBox.Text) ? Visibility.Visible : Visibility.Collapsed; };
            filterBox.SelectionChanged += delegate { RefreshNoteList(); };
            Deactivated += delegate { if (!isCollapsed && !appData.PinnedOpen) collapseTimer.Start(); };
            MouseEnter += delegate { collapseTimer.Stop(); };
            Closing += OnClosing;
            SizeChanged += delegate
            {
                if (!isCollapsed && WindowState == WindowState.Normal)
                {
                    expandedWidth = ActualWidth;
                    expandedHeight = ActualHeight;
                }
            };
            PreviewKeyDown += OnPreviewKeyDown;
        }

        private void BuildTrayIcon()
        {
            notebookIcon = CreateNotebookIcon();
            trayIcon = new Forms.NotifyIcon();
            trayIcon.Icon = notebookIcon;
            trayIcon.Text = "栖笺 PerchNote";
            trayIcon.Visible = true;
            Icon = Imaging.CreateBitmapSourceFromHIcon(notebookIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
            menu.Font = new Drawing.Font("Microsoft YaHei UI", 9F);
            menu.ShowImageMargin = false;
            menu.Items.Add("展开栖笺", null, delegate { Dispatcher.BeginInvoke(new Action(ShowExpanded)); });
            menu.Items.Add("新建待办", null, delegate { Dispatcher.BeginInvoke(new Action(delegate { ShowExpanded(); CreateNote(NoteKind.Todo); })); });
            menu.Items.Add("新建灵感", null, delegate { Dispatcher.BeginInvoke(new Action(delegate { ShowExpanded(); CreateNote(NoteKind.Idea); })); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            Forms.ToolStripMenuItem exportItem = new Forms.ToolStripMenuItem("导出当前便签");
            exportItem.DropDownItems.Add("导出为 Markdown…", null, delegate { Dispatcher.BeginInvoke(new Action(ExportCurrentNoteAsMarkdown)); });
            exportItem.DropDownItems.Add("导出为 PDF…", null, delegate { Dispatcher.BeginInvoke(new Action(ExportCurrentNoteAsPdf)); });
            menu.Items.Add(exportItem);
            trayTopmostItem = new Forms.ToolStripMenuItem("窗口置顶");
            trayTopmostItem.Checked = appData.Topmost;
            trayTopmostItem.Click += delegate { Dispatcher.BeginInvoke(new Action(ToggleTopmostSetting)); };
            menu.Items.Add(trayTopmostItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { Dispatcher.BeginInvoke(new Action(ExitApplication)); });
            trayIcon.ContextMenuStrip = menu;
            trayIcon.DoubleClick += delegate { Dispatcher.BeginInvoke(new Action(ShowExpanded)); };
        }

        private Drawing.Icon CreateNotebookIcon()
        {
            using (Drawing.Bitmap bitmap = new Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (Drawing.Graphics graphics = Drawing.Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
                graphics.Clear(Drawing.Color.Transparent);
                using (Drawing2D.GraphicsPath cover = RoundedRectangle(new Drawing.RectangleF(4, 3, 25, 27), 5))
                using (Drawing.SolidBrush coverBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 91, 119, 96)))
                    graphics.FillPath(coverBrush, cover);
                using (Drawing2D.GraphicsPath page = RoundedRectangle(new Drawing.RectangleF(9, 6, 16, 21), 2.5F))
                using (Drawing.SolidBrush pageBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 250, 247, 237)))
                    graphics.FillPath(pageBrush, page);
                using (Drawing.SolidBrush spine = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 199, 150, 96)))
                    graphics.FillRectangle(spine, 6, 5, 4, 23);
                using (Drawing.Pen ringPen = new Drawing.Pen(Drawing.Color.FromArgb(255, 225, 220, 205), 1.5F))
                {
                    ringPen.StartCap = Drawing2D.LineCap.Round;
                    ringPen.EndCap = Drawing2D.LineCap.Round;
                    for (int y = 8; y <= 24; y += 4) graphics.DrawLine(ringPen, 3.5F, y, 8.5F, y);
                }
                using (Drawing.Pen linePen = new Drawing.Pen(Drawing.Color.FromArgb(190, 103, 126, 107), 1.3F))
                {
                    linePen.StartCap = Drawing2D.LineCap.Round;
                    linePen.EndCap = Drawing2D.LineCap.Round;
                    graphics.DrawLine(linePen, 13, 12, 22, 12);
                    graphics.DrawLine(linePen, 13, 16, 22, 16);
                    graphics.DrawLine(linePen, 13, 20, 19, 20);
                }
                IntPtr handle = bitmap.GetHicon();
                try { return (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone(); }
                finally { DestroyIcon(handle); }
            }
        }

        private Drawing2D.GraphicsPath RoundedRectangle(Drawing.RectangleF rect, float radius)
        {
            Drawing2D.GraphicsPath path = new Drawing2D.GraphicsPath();
            float diameter = radius * 2;
            path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        private void SelectInitialNote()
        {
            RefreshNoteList();
            Note note = appData.Notes.FirstOrDefault(n => n.Id == appData.SelectedId) ?? appData.Notes.OrderByDescending(n => n.UpdatedAt).First();
            SelectNote(note);
        }

        private void RefreshNoteList()
        {
            if (noteList == null) return;
            string query = searchBox == null ? "" : searchBox.Text.Trim();
            int filter = filterBox == null ? 0 : filterBox.SelectedIndex;
            string selectedId = currentNote == null ? appData.SelectedId : currentNote.Id;
            noteList.Items.Clear();
            IEnumerable<Note> notes = appData.Notes.OrderByDescending(n => n.UpdatedAt);
            if (filter == 1) notes = notes.Where(n => n.Kind == NoteKind.Todo);
            if (filter == 2) notes = notes.Where(n => n.Kind == NoteKind.Idea);
            if (query.Length > 0) notes = notes.Where(n => (n.Title ?? "").IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 || (n.Content ?? "").IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0);
            foreach (Note note in notes)
            {
                NoteListItem item = new NoteListItem(note);
                noteList.Items.Add(item);
                if (note.Id == selectedId) noteList.SelectedItem = item;
            }
        }

        private void OnNoteSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            NoteListItem item = noteList.SelectedItem as NoteListItem;
            if (item != null && item.Note != currentNote) SelectNote(item.Note);
        }

        private void SelectNote(Note note)
        {
            if (note == null) return;
            FlushCurrentNote();
            currentNote = note;
            appData.SelectedId = note.Id;
            isLoadingNote = true;
            titleBox.Text = note.Title ?? "";
            editor.Text = note.Content ?? "";
            isLoadingNote = false;
            RenderPreview();
            UpdateStatus();
        }

        private void OnNoteEdited(object sender, TextChangedEventArgs e)
        {
            if (isLoadingNote || currentNote == null) return;
            FlushCurrentNote();
            if (sender == editor) RenderPreview();
            saveStatus.Text = "正在自动保存…";
            saveTimer.Stop();
            saveTimer.Start();
            UpdateStatus();
        }

        private void FlushCurrentNote()
        {
            if (currentNote == null || titleBox == null || editor == null) return;
            currentNote.Title = string.IsNullOrWhiteSpace(titleBox.Text) ? "未命名" : titleBox.Text.Trim();
            currentNote.Content = editor.Text ?? "";
            currentNote.UpdatedAt = DateTime.Now;
        }

        private void RenderPreview()
        {
            preview.Document = MarkdownRenderer.Render(editor.Text ?? "");
        }

        private void UpdateStatus()
        {
            string text = editor == null ? "" : editor.Text;
            int lines = string.IsNullOrEmpty(text) ? 0 : text.Replace("\r\n", "\n").Split('\n').Length;
            countStatus.Text = text.Length + " 字符 · " + lines + " 行";
            if (kindStatus != null && currentNote != null)
            {
                kindStatus.Text = currentNote.Kind == NoteKind.Todo ? "●  待办" : "●  灵感";
                kindStatus.Foreground = new SolidColorBrush(currentNote.Kind == NoteKind.Todo ? Accent : IdeaAccent);
            }
        }

        private void CreateNote(NoteKind kind)
        {
            FlushCurrentNote();
            Note note = new Note();
            note.Kind = kind;
            note.Title = kind == NoteKind.Todo ? "新的待办" : "新的灵感";
            note.Content = kind == NoteKind.Todo ? "# 待办\n\n- [ ] " : "# 灵感\n\n";
            appData.Notes.Add(note);
            appData.SelectedId = note.Id;
            filterBox.SelectedIndex = 0;
            searchBox.Text = "";
            RefreshNoteList();
            SelectNote(note);
            SaveNow();
            titleBox.SelectAll();
            titleBox.Focus();
        }

        private void DeleteCurrentNote()
        {
            if (currentNote == null) return;
            MessageBoxResult answer = MessageBox.Show("确定删除“" + currentNote.Title + "”吗？", "删除便签", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            appData.Notes.Remove(currentNote);
            currentNote = null;
            EnsureFirstNote();
            RefreshNoteList();
            SelectNote(appData.Notes.OrderByDescending(n => n.UpdatedAt).First());
            SaveNow();
        }

        private void SaveNow()
        {
            try
            {
                FlushCurrentNote();
                appData.ExpandedWidth = expandedWidth;
                appData.ExpandedHeight = expandedHeight;
                Directory.CreateDirectory(dataDirectory);
                string json = new JavaScriptSerializer().Serialize(appData);
                string temp = dataPath + ".tmp";
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                if (File.Exists(dataPath))
                {
                    string backup = dataPath + ".bak";
                    File.Replace(temp, dataPath, backup, true);
                }
                else File.Move(temp, dataPath);
                if (saveStatus != null) saveStatus.Text = "已保存 · " + DateTime.Now.ToString("HH:mm:ss");
                RefreshNoteList();
            }
            catch (Exception ex)
            {
                if (saveStatus != null) saveStatus.Text = "保存失败：" + ex.Message;
            }
        }

        private void TogglePinned(object sender, RoutedEventArgs e)
        {
            appData.PinnedOpen = !appData.PinnedOpen;
            pinButton.Content = appData.PinnedOpen ? "●  固定" : "○  固定";
            collapseTimer.Stop();
            SaveNow();
        }

        private void ToggleTopmostSetting()
        {
            appData.Topmost = !appData.Topmost;
            Topmost = appData.Topmost;
            if (trayTopmostItem != null) trayTopmostItem.Checked = appData.Topmost;
            SaveNow();
        }

        private void ExportCurrentNoteAsMarkdown()
        {
            if (currentNote == null) return;
            FlushCurrentNote();
            using (Forms.SaveFileDialog dialog = new Forms.SaveFileDialog())
            {
                dialog.Title = "导出当前便签为 Markdown";
                dialog.Filter = "Markdown 文件 (*.md)|*.md";
                dialog.DefaultExt = "md";
                dialog.AddExtension = true;
                dialog.FileName = SafeExportName(currentNote.Title) + ".md";
                if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
                string markdown = "# " + currentNote.Title + Environment.NewLine + Environment.NewLine + (currentNote.Content ?? "");
                File.WriteAllText(dialog.FileName, markdown, new UTF8Encoding(false));
                NotifyExportCompleted("Markdown 已导出", dialog.FileName);
            }
        }

        private void ExportCurrentNoteAsPdf()
        {
            if (currentNote == null) return;
            FlushCurrentNote();
            using (Forms.SaveFileDialog dialog = new Forms.SaveFileDialog())
            {
                dialog.Title = "导出当前便签为 PDF";
                dialog.Filter = "PDF 文件 (*.pdf)|*.pdf";
                dialog.DefaultExt = "pdf";
                dialog.AddExtension = true;
                dialog.FileName = SafeExportName(currentNote.Title) + ".pdf";
                if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
                try
                {
                    PdfExporter.Export(currentNote, dialog.FileName);
                    NotifyExportCompleted("PDF 已导出", dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("PDF 导出失败：" + ex.Message, "栖笺", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private string SafeExportName(string title)
        {
            string result = string.IsNullOrWhiteSpace(title) ? "栖笺" : title.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars()) result = result.Replace(invalid, '_');
            return result.Length > 60 ? result.Substring(0, 60) : result;
        }

        private void NotifyExportCompleted(string title, string path)
        {
            trayIcon.BalloonTipTitle = title;
            trayIcon.BalloonTipText = path;
            trayIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
            trayIcon.ShowBalloonTip(2500);
        }

        private void ShowCollapsed()
        {
            if (isCollapsed) return;
            SaveNow();
            expandedWidth = ActualWidth;
            expandedHeight = ActualHeight;
            isCollapsed = true;
            Content = collapsedView;
            ResizeMode = ResizeMode.NoResize;
            MinWidth = 0;
            MinHeight = 0;
            Width = 34;
            Height = 132;
            ShowInTaskbar = false;
            PositionCollapsed();
        }

        private void ShowExpanded()
        {
            collapseTimer.Stop();
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            isCollapsed = false;
            Content = expandedView;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            MinWidth = 720;
            MinHeight = 480;
            Width = Math.Max(720, expandedWidth);
            Height = Math.Max(480, expandedHeight);
            ShowInTaskbar = true;
            PositionAtRightEdge();
            Activate();
            Topmost = appData.Topmost;
            if (editor != null) editor.Focus();
        }

        private void PositionAtRightEdge()
        {
            Rect work = SystemParameters.WorkArea;
            Left = Math.Max(work.Left + 8, work.Right - Width - 14);
            Top = Math.Max(work.Top + 8, work.Top + (work.Height - Height) / 2);
        }

        private void PositionCollapsed()
        {
            Rect work = SystemParameters.WorkArea;
            Left = work.Right - Width;
            Top = work.Top + (work.Height - Height) / 2;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape || (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.M))
            {
                ShowCollapsed(); e.Handled = true; return;
            }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
            {
                CreateNote(NoteKind.Idea); e.Handled = true; return;
            }
            if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.N)
            {
                CreateNote(NoteKind.Todo); e.Handled = true; return;
            }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
            {
                SaveNow(); e.Handled = true; return;
            }
            if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Delete)
            {
                DeleteCurrentNote(); e.Handled = true; return;
            }
            if (editor.IsKeyboardFocusWithin && Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.B)
            {
                WrapSelection("**", "**"); e.Handled = true; return;
            }
            if (editor.IsKeyboardFocusWithin && Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.I)
            {
                WrapSelection("*", "*"); e.Handled = true;
            }
        }

        private void WrapSelection(string before, string after)
        {
            int start = editor.SelectionStart;
            string selected = editor.SelectedText;
            editor.SelectedText = before + selected + after;
            editor.SelectionStart = start + before.Length;
            editor.SelectionLength = selected.Length;
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveNow();
            if (!reallyExit)
            {
                e.Cancel = true;
                Hide();
            }
        }

        private void ExitApplication()
        {
            reallyExit = true;
            SaveNow();
            if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); }
            if (notebookIcon != null) notebookIcon.Dispose();
            Close();
            Application.Current.Shutdown();
        }
    }

    internal static class MarkdownRenderer
    {
        private static readonly Brush InkBrush = new SolidColorBrush(Color.FromRgb(48, 49, 47));
        private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(122, 123, 119));
        private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(103, 126, 107));
        private static readonly Brush CodeBrush = new SolidColorBrush(Color.FromRgb(238, 239, 234));
        private static readonly Regex InlinePattern = new Regex(@"(\*\*.+?\*\*|~~.+?~~|`.+?`|\*[^*\r\n]+?\*|\[[^\]]+\]\([^)]+\))", RegexOptions.Compiled);

        public static FlowDocument Render(string markdown)
        {
            FlowDocument document = new FlowDocument();
            document.FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI");
            document.FontSize = 14.2;
            document.Foreground = InkBrush;
            document.PagePadding = new Thickness(12, 10, 12, 18);
            document.LineHeight = 23;
            document.TextAlignment = TextAlignment.Left;

            string[] lines = (markdown ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool inCode = false;
            StringBuilder code = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.TrimStart().StartsWith("```"))
                {
                    if (!inCode) { inCode = true; code.Clear(); }
                    else { AddCodeBlock(document, code.ToString().TrimEnd('\n')); inCode = false; }
                    continue;
                }
                if (inCode) { code.AppendLine(line); continue; }
                if (string.IsNullOrWhiteSpace(line))
                {
                    Paragraph spacer = new Paragraph { Margin = new Thickness(0, 0, 0, 5), FontSize = 4 };
                    document.Blocks.Add(spacer);
                    continue;
                }

                Match heading = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
                if (heading.Success)
                {
                    int level = heading.Groups[1].Value.Length;
                    Paragraph p = NewParagraph(level <= 2 ? 9 : 5);
                    p.FontSize = level == 1 ? 28 : level == 2 ? 22 : level == 3 ? 18 : 15;
                    p.FontWeight = FontWeights.Bold;
                    AddInline(p, heading.Groups[2].Value);
                    document.Blocks.Add(p);
                    continue;
                }
                if (Regex.IsMatch(line.Trim(), @"^([-*_])\1{2,}$"))
                {
                    BlockUIContainer rule = new BlockUIContainer(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(223, 222, 216)), Margin = new Thickness(0, 8, 0, 9) });
                    document.Blocks.Add(rule);
                    continue;
                }
                Match task = Regex.Match(line, @"^\s*[-*+]\s+\[([ xX])\]\s*(.*)$");
                if (task.Success)
                {
                    bool done = task.Groups[1].Value != " ";
                    Paragraph p = NewParagraph(3);
                    Run box = new Run(done ? "☑  " : "☐  ") { Foreground = done ? MutedBrush : AccentBrush, FontSize = 16 };
                    p.Inlines.Add(box);
                    AddInline(p, task.Groups[2].Value);
                    if (done) { p.TextDecorations = TextDecorations.Strikethrough; p.Foreground = MutedBrush; }
                    document.Blocks.Add(p);
                    continue;
                }
                Match quote = Regex.Match(line, @"^\s*>\s?(.*)$");
                if (quote.Success)
                {
                    Border border = new Border { BorderBrush = AccentBrush, BorderThickness = new Thickness(3, 0, 0, 0), Background = new SolidColorBrush(Color.FromRgb(239, 243, 238)), CornerRadius = new CornerRadius(0, 6, 6, 0), Padding = new Thickness(11, 7, 9, 7), Margin = new Thickness(0, 3, 0, 7) };
                    TextBlock text = new TextBlock { Text = StripInline(quote.Groups[1].Value), TextWrapping = TextWrapping.Wrap, Foreground = MutedBrush, FontStyle = FontStyles.Italic };
                    border.Child = text;
                    document.Blocks.Add(new BlockUIContainer(border));
                    continue;
                }
                Match bullet = Regex.Match(line, @"^\s*[-*+]\s+(.+)$");
                Match numbered = Regex.Match(line, @"^\s*(\d+)\.\s+(.+)$");
                if (bullet.Success || numbered.Success)
                {
                    Paragraph p = NewParagraph(3);
                    p.Margin = new Thickness(10, 1, 0, 3);
                    p.Inlines.Add(new Run(bullet.Success ? "•  " : numbered.Groups[1].Value + ".  ") { Foreground = AccentBrush, FontWeight = FontWeights.Bold });
                    AddInline(p, bullet.Success ? bullet.Groups[1].Value : numbered.Groups[2].Value);
                    document.Blocks.Add(p);
                    continue;
                }
                Paragraph normal = NewParagraph(6);
                AddInline(normal, line);
                document.Blocks.Add(normal);
            }
            if (inCode) AddCodeBlock(document, code.ToString().TrimEnd('\n'));
            if (document.Blocks.Count == 0)
            {
                Paragraph empty = NewParagraph(0);
                empty.Inlines.Add(new Run("预览会显示在这里…") { Foreground = MutedBrush, FontStyle = FontStyles.Italic });
                document.Blocks.Add(empty);
            }
            return document;
        }

        private static Paragraph NewParagraph(double bottom)
        {
            return new Paragraph { Margin = new Thickness(0, 0, 0, bottom), LineHeight = 23 };
        }

        private static void AddCodeBlock(FlowDocument document, string text)
        {
            TextBox code = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5, Foreground = InkBrush };
            Border box = new Border { Background = CodeBrush, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 3, 0, 7), Child = code };
            document.Blocks.Add(new BlockUIContainer(box));
        }

        private static void AddInline(Paragraph paragraph, string text)
        {
            int index = 0;
            foreach (Match match in InlinePattern.Matches(text))
            {
                if (match.Index > index) paragraph.Inlines.Add(new Run(text.Substring(index, match.Index - index)));
                string token = match.Value;
                if (token.StartsWith("**")) paragraph.Inlines.Add(new Run(token.Substring(2, token.Length - 4)) { FontWeight = FontWeights.Bold });
                else if (token.StartsWith("~~")) paragraph.Inlines.Add(new Run(token.Substring(2, token.Length - 4)) { TextDecorations = TextDecorations.Strikethrough, Foreground = MutedBrush });
                else if (token.StartsWith("`")) paragraph.Inlines.Add(new Run(token.Substring(1, token.Length - 2)) { FontFamily = new FontFamily("Consolas"), Background = CodeBrush, Foreground = AccentBrush });
                else if (token.StartsWith("*")) paragraph.Inlines.Add(new Run(token.Substring(1, token.Length - 2)) { FontStyle = FontStyles.Italic });
                else if (token.StartsWith("["))
                {
                    Match link = Regex.Match(token, @"^\[([^\]]+)\]\(([^)]+)\)$");
                    Uri uri;
                    if (link.Success && Uri.TryCreate(link.Groups[2].Value, UriKind.Absolute, out uri))
                    {
                        Hyperlink hyperlink = new Hyperlink(new Run(link.Groups[1].Value)) { NavigateUri = uri, Foreground = AccentBrush };
                        hyperlink.RequestNavigate += delegate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
                        {
                            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
                        };
                        paragraph.Inlines.Add(hyperlink);
                    }
                    else paragraph.Inlines.Add(new Run(token));
                }
                index = match.Index + match.Length;
            }
            if (index < text.Length) paragraph.Inlines.Add(new Run(text.Substring(index)));
        }

        private static string StripInline(string text)
        {
            return Regex.Replace(text, @"(\*\*|~~|`|\*)", "");
        }
    }

    internal static class PdfExporter
    {
        private const double PageWidth = 794;
        private const double PageHeight = 1123;
        private const int ImageWidth = 1191;
        private const int ImageHeight = 1685;
        private const double PdfWidth = 595.28;
        private const double PdfHeight = 841.89;

        public static void Export(Note note, string path)
        {
            if (note == null) throw new ArgumentNullException("note");
            FlowDocument document = MarkdownRenderer.Render(note.Content ?? "");
            document.PageWidth = PageWidth;
            document.PageHeight = PageHeight;
            document.PagePadding = new Thickness(66, 62, 66, 76);
            document.ColumnWidth = double.PositiveInfinity;
            document.ColumnGap = 0;

            Paragraph title = new Paragraph(new Run(string.IsNullOrWhiteSpace(note.Title) ? "未命名便签" : note.Title));
            title.FontFamily = new FontFamily("Segoe UI Variable Display, Microsoft YaHei UI, Segoe UI");
            title.FontSize = 30;
            title.FontWeight = FontWeights.SemiBold;
            title.Foreground = new SolidColorBrush(Color.FromRgb(48, 49, 47));
            title.Margin = new Thickness(0, 0, 0, 8);
            Paragraph meta = new Paragraph(new Run((note.Kind == NoteKind.Todo ? "待办事项" : "灵感记录") + "   ·   更新于 " + note.UpdatedAt.ToString("yyyy-MM-dd HH:mm")));
            meta.FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI");
            meta.FontSize = 10.5;
            meta.Foreground = new SolidColorBrush(Color.FromRgb(122, 123, 119));
            meta.Margin = new Thickness(0, 0, 0, 24);
            Block first = document.Blocks.FirstBlock;
            if (first != null)
            {
                document.Blocks.InsertBefore(first, meta);
                document.Blocks.InsertBefore(meta, title);
            }
            else
            {
                document.Blocks.Add(title);
                document.Blocks.Add(meta);
            }

            DocumentPaginator paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
            paginator.PageSize = new Size(PageWidth, PageHeight);
            paginator.ComputePageCount();
            int pageCount = Math.Max(1, paginator.PageCount);
            List<byte[]> images = new List<byte[]>();
            for (int index = 0; index < pageCount; index++) images.Add(RenderPage(paginator.GetPage(index), index + 1, pageCount));
            WriteImagePdf(path, images);
        }

        private static byte[] RenderPage(DocumentPage page, int pageNumber, int pageCount)
        {
            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                context.DrawRectangle(Brushes.White, null, new Rect(0, 0, PageWidth, PageHeight));
                if (page != null && page.Visual != null)
                {
                    VisualBrush pageBrush = new VisualBrush(page.Visual) { Stretch = Stretch.Fill };
                    context.DrawRectangle(pageBrush, null, new Rect(0, 0, PageWidth, PageHeight));
                }
                Pen footerRule = new Pen(new SolidColorBrush(Color.FromRgb(228, 227, 222)), 1);
                context.DrawLine(footerRule, new Point(66, PageHeight - 53), new Point(PageWidth - 66, PageHeight - 53));
                FormattedText brand = new FormattedText("栖笺 PerchNote", CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 9.5, new SolidColorBrush(Color.FromRgb(122, 123, 119)), 1.0);
                context.DrawText(brand, new Point(66, PageHeight - 40));
                FormattedText number = new FormattedText(pageNumber + " / " + pageCount, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 9.5, new SolidColorBrush(Color.FromRgb(122, 123, 119)), 1.0);
                context.DrawText(number, new Point(PageWidth - 66 - number.Width, PageHeight - 40));
            }
            RenderTargetBitmap bitmap = new RenderTargetBitmap(ImageWidth, ImageHeight, 144, 144, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            JpegBitmapEncoder encoder = new JpegBitmapEncoder { QualityLevel = 94 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (MemoryStream stream = new MemoryStream())
            {
                encoder.Save(stream);
                return stream.ToArray();
            }
        }

        private static void WriteImagePdf(string path, IList<byte[]> images)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            int objectCount = 2 + images.Count * 3;
            long[] offsets = new long[objectCount + 1];
            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                WriteAscii(stream, "%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");
                offsets[1] = stream.Position;
                WriteAscii(stream, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
                offsets[2] = stream.Position;
                StringBuilder kids = new StringBuilder();
                for (int i = 0; i < images.Count; i++) kids.Append(3 + i * 3).Append(" 0 R ");
                WriteAscii(stream, "2 0 obj\n<< /Type /Pages /Count " + images.Count + " /Kids [" + kids + "] >>\nendobj\n");

                for (int i = 0; i < images.Count; i++)
                {
                    int pageObject = 3 + i * 3;
                    int contentObject = pageObject + 1;
                    int imageObject = pageObject + 2;
                    offsets[pageObject] = stream.Position;
                    WriteAscii(stream, pageObject + " 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + PdfWidth.ToString("0.##", CultureInfo.InvariantCulture) + " " + PdfHeight.ToString("0.##", CultureInfo.InvariantCulture) + "] /Resources << /XObject << /Im0 " + imageObject + " 0 R >> >> /Contents " + contentObject + " 0 R >>\nendobj\n");
                    string content = "q\n" + PdfWidth.ToString("0.##", CultureInfo.InvariantCulture) + " 0 0 " + PdfHeight.ToString("0.##", CultureInfo.InvariantCulture) + " 0 0 cm\n/Im0 Do\nQ\n";
                    offsets[contentObject] = stream.Position;
                    WriteAscii(stream, contentObject + " 0 obj\n<< /Length " + Encoding.ASCII.GetByteCount(content) + " >>\nstream\n" + content + "endstream\nendobj\n");
                    offsets[imageObject] = stream.Position;
                    WriteAscii(stream, imageObject + " 0 obj\n<< /Type /XObject /Subtype /Image /Width " + ImageWidth + " /Height " + ImageHeight + " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " + images[i].Length + " >>\nstream\n");
                    stream.Write(images[i], 0, images[i].Length);
                    WriteAscii(stream, "\nendstream\nendobj\n");
                }

                long xref = stream.Position;
                WriteAscii(stream, "xref\n0 " + (objectCount + 1) + "\n0000000000 65535 f \n");
                for (int i = 1; i <= objectCount; i++) WriteAscii(stream, offsets[i].ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
                WriteAscii(stream, "trailer\n<< /Size " + (objectCount + 1) + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
            }
        }

        private static void WriteAscii(Stream stream, string value)
        {
            byte[] bytes = Encoding.GetEncoding(1252).GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            if (args != null && args.Length == 2 && args[0] == "--export-sample-pdf")
            {
                Application renderer = new Application();
                Note sample = new Note
                {
                    Title = "栖笺 PDF 导出示例",
                    Kind = NoteKind.Idea,
                    UpdatedAt = DateTime.Now,
                    Content = "# 设计得更轻盈\n\n这是一个用于验证 **Markdown 与中文字体** 的导出示例。\n\n## 今日清单\n\n- [x] 优化系统托盘图标\n- [x] 将窗口置顶移到右键菜单\n- [ ] 写下下一条灵感\n\n> 好的工具应该安静地栖息在屏幕边缘，需要时再出现。\n\n`Ctrl + M` 可以让栖笺回到屏幕边缘。"
                };
                PdfExporter.Export(sample, args[1]);
                renderer.Shutdown();
                return;
            }
            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Run(new MainWindow());
        }
    }
}
