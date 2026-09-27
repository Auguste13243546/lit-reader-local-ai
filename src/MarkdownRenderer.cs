using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace LitReader
{
    // 轻量 Markdown 渲染器（零依赖）。
    // 模型输出的是 Markdown，而 TextBlock 不会解析，所以先前的回答会把
    // **、###、- 这类符号原样显示出来。
    //
    // 支持：标题、无序/有序列表、引用、分割线、代码块、表格，
    //       行内 **粗体** / *斜体* / `代码`
    internal static class MarkdownRenderer
    {
        // 新粗野主义配色：白纸黑字、纯黑硬边、高饱和强调色
        static Brush Fg = new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00));
        static Brush FgDim = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A));
        static Brush Accent = new SolidColorBrush(Color.FromRgb(0x00, 0x38, 0xFF));   // 电光蓝
        static Brush Pink = new SolidColorBrush(Color.FromRgb(0xFF, 0x3E, 0xA5));     // 荧光粉
        static Brush Mint = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xA0));     // 薄荷绿
        static Brush Yellow = new SolidColorBrush(Color.FromRgb(0xFF, 0xE6, 0x00));   // 亮黄
        static Brush Paper = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        static Brush LineBr = new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00));

        static readonly FontFamily UI = new FontFamily("Bahnschrift SemiBold, Microsoft YaHei UI");
        static readonly FontFamily Display = new FontFamily("Arial Black, Microsoft YaHei UI");
        static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas");

        public static UIElement Render(string md)
        {
            var panel = new StackPanel();
            if (string.IsNullOrEmpty(md)) return panel;

            string[] lines = md.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int i = 0;

            while (i < lines.Length)
            {
                string raw = lines[i];
                string t = raw.Trim();

                // 空行
                if (t.Length == 0) { i++; continue; }

                // 代码块 ```
                if (t.StartsWith("```"))
                {
                    var code = new StringBuilder();
                    i++;
                    while (i < lines.Length && !lines[i].Trim().StartsWith("```"))
                    {
                        code.Append(lines[i]).Append('\n');
                        i++;
                    }
                    if (i < lines.Length) i++;      // 跳过收尾 ```
                    panel.Children.Add(CodeBlock(code.ToString().TrimEnd('\n')));
                    continue;
                }

                // 分割线
                if (IsRule(t)) { panel.Children.Add(Rule()); i++; continue; }

                // 标题
                int level = 0;
                while (level < t.Length && t[level] == '#') level++;
                if (level >= 1 && level <= 6 && level < t.Length && t[level] == ' ')
                {
                    panel.Children.Add(Header(t.Substring(level).Trim(), level));
                    i++;
                    continue;
                }

                // 表格（| a | b | 且下一行是 |---|---|）
                if (t.StartsWith("|") && t.EndsWith("|") && i + 1 < lines.Length && IsTableSep(lines[i + 1]))
                {
                    var rows = new List<string[]>();
                    rows.Add(SplitRow(t));
                    i += 2;
                    while (i < lines.Length && lines[i].Trim().StartsWith("|") && lines[i].Trim().EndsWith("|"))
                    {
                        rows.Add(SplitRow(lines[i].Trim()));
                        i++;
                    }
                    panel.Children.Add(Table(rows));
                    continue;
                }

                // 引用
                if (t.StartsWith(">"))
                {
                    var quote = new StringBuilder();
                    while (i < lines.Length && lines[i].Trim().StartsWith(">"))
                    {
                        quote.Append(lines[i].Trim().TrimStart('>').Trim()).Append('\n');
                        i++;
                    }
                    panel.Children.Add(Quote(quote.ToString().TrimEnd('\n')));
                    continue;
                }

                // 无序列表
                if (IsBullet(t))
                {
                    panel.Children.Add(ListItem(t.Substring(1).TrimStart(), "•"));
                    i++;
                    continue;
                }

                // 有序列表
                int num;
                if (TryNumbered(t, out num))
                {
                    int dot = t.IndexOf('.');
                    panel.Children.Add(ListItem(t.Substring(dot + 1).TrimStart(), num + "."));
                    i++;
                    continue;
                }

                // 普通段落
                panel.Children.Add(Paragraph(t));
                i++;
            }

            return panel;
        }

        // ---------- 块级元素 ----------
        static bool IsRule(string t)
        {
            if (t.Length < 3) return false;
            char c = t[0];
            if (c != '-' && c != '*' && c != '_') return false;
            for (int i = 0; i < t.Length; i++) if (t[i] != c) return false;
            return true;
        }

        static bool IsTableSep(string line)
        {
            string t = line.Trim();
            if (!t.StartsWith("|") || !t.EndsWith("|")) return false;
            foreach (char c in t) if (c != '|' && c != '-' && c != ':' && c != ' ') return false;
            return t.IndexOf('-') >= 0;
        }

        static string[] SplitRow(string t)
        {
            string[] parts = t.Trim('|').Split('|');
            for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
            return parts;
        }

        static bool IsBullet(string t)
        {
            if (t.Length < 2) return false;
            if (t[0] != '-' && t[0] != '*' && t[0] != '+') return false;
            return t[1] == ' ';
        }

        static bool TryNumbered(string t, out int n)
        {
            n = 0;
            int i = 0;
            while (i < t.Length && char.IsDigit(t[i])) i++;
            if (i == 0 || i + 1 >= t.Length) return false;
            if (t[i] != '.' || t[i + 1] != ' ') return false;
            int.TryParse(t.Substring(0, i), out n);
            return true;
        }

        static UIElement Header(string text, int level)
        {
            double size = 15 - (level - 1) * 0.8;
            if (size < 12) size = 12;
            var tb = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = size,
                FontWeight = FontWeights.SemiBold,
                Foreground = level <= 2 ? Accent : Fg,
                Margin = new Thickness(0, 7, 0, 3)
            };
            AddInlines(tb, text, true);
            return tb;
        }

        static UIElement Paragraph(string text)
        {
            var tb = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Fg,
                FontSize = 13,
                LineHeight = 20,
                Margin = new Thickness(0, 1, 0, 3)
            };
            AddInlines(tb, text, false);
            return tb;
        }

        static UIElement ListItem(string text, string marker)
        {
            var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var mk = new TextBlock
            {
                Text = marker,
                Foreground = Accent,
                FontSize = 13,
                Margin = new Thickness(2, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetColumn(mk, 0);

            var tb = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Fg,
                FontSize = 13,
                LineHeight = 20
            };
            AddInlines(tb, text, false);
            Grid.SetColumn(tb, 1);

            g.Children.Add(mk);
            g.Children.Add(tb);
            return g;
        }

        static UIElement Quote(string text)
        {
            var border = new Border
            {
                BorderBrush = Accent,
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(9, 3, 0, 3),
                Margin = new Thickness(0, 3, 0, 4)
            };
            var tb = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = FgDim,
                FontSize = 13,
                LineHeight = 20,
                FontStyle = FontStyles.Italic
            };
            AddInlines(tb, text, false);
            border.Child = tb;
            return border;
        }

        static UIElement Rule()
        {
            return new Border
            {
                Height = 1,
                Background = LineBr,
                Margin = new Thickness(0, 6, 0, 6)
            };
        }

        static UIElement CodeBlock(string code)
        {
            var tb = new TextBlock
            {
                Text = code,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = Mono,
                FontSize = 12,
                Foreground = Fg,
                LineHeight = 18
            };
            // 硬边矩形 + 粗黑边 + 亮黄底（像贴纸）
            return new Border
            {
                Background = Yellow,
                BorderBrush = Fg,
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 5, 0, 6),
                Child = tb
            };
        }

        static UIElement Table(List<string[]> rows)
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 6) };
            int cols = 0;
            foreach (var r in rows) if (r.Length > cols) cols = r.Length;
            for (int c = 0; c < cols; c++)
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int r = 0; r < rows.Count; r++)
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int r = 0; r < rows.Count; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    string cell = c < rows[r].Length ? rows[r][c] : "";
                    var tb = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Foreground = r == 0 ? Fg : FgDim,
                        FontWeight = r == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                        Margin = new Thickness(2, 3, 8, 3)
                    };
                    AddInlines(tb, cell, r == 0);
                    Grid.SetRow(tb, r);
                    Grid.SetColumn(tb, c);
                    g.Children.Add(tb);
                }
            }
            return new Border
            {
                BorderBrush = LineBr,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 2, 6, 2),
                Child = g
            };
        }

        // ---------- 行内解析：**粗体** / *斜体* / `代码` ----------
        static void AddInlines(TextBlock tb, string text, bool bold)
        {
            if (string.IsNullOrEmpty(text)) return;
            int i = 0;
            var buf = new StringBuilder();

            Action flush = delegate
            {
                if (buf.Length == 0) return;
                var run = new Run(buf.ToString()) { Foreground = Fg };
                if (bold) run.FontWeight = FontWeights.SemiBold;
                tb.Inlines.Add(run);
                buf.Length = 0;
            };

            while (i < text.Length)
            {
                char c = text[i];

                // `code`
                if (c == '`')
                {
                    int end = text.IndexOf('`', i + 1);
                    if (end > i)
                    {
                        flush();
                        tb.Inlines.Add(new Run(text.Substring(i + 1, end - i - 1))
                        {
                            FontFamily = Mono,
                            FontSize = 12,
                            Foreground = Fg,
                            Background = Yellow
                        });
                        i = end + 1;
                        continue;
                    }
                }

                // **bold**
                if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                    if (end > i + 1)
                    {
                        flush();
                        tb.Inlines.Add(new Run(text.Substring(i + 2, end - i - 2))
                        {
                            FontWeight = FontWeights.Bold,
                            Foreground = Fg
                        });
                        i = end + 2;
                        continue;
                    }
                }

                // *italic*
                if (c == '*')
                {
                    int end = text.IndexOf('*', i + 1);
                    if (end > i + 1)
                    {
                        flush();
                        tb.Inlines.Add(new Run(text.Substring(i + 1, end - i - 1))
                        {
                            FontStyle = FontStyles.Italic,
                            Foreground = Fg
                        });
                        i = end + 1;
                        continue;
                    }
                }

                buf.Append(c);
                i++;
            }
            flush();
        }
    }
}
