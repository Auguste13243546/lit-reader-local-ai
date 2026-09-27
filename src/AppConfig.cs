using System;
using System.IO;
using System.Text;

namespace LitReader
{
    // 运行期配置。
    //
    // 开源版本不包含任何硬编码的个人路径：数据目录与 Ollama 地址都从
    // 环境变量或程序同目录下的 config.txt 读取，都有合理默认值。
    //
    // 优先级：环境变量 > config.txt > 默认值
    internal static class AppConfig
    {
        // Ollama 服务地址（本地默认）
        public static string OllamaBase = "http://127.0.0.1:11434";

        // 数据目录：存放长期记忆、学术词汇、术语本等
        // 默认放在程序同目录的 data 子目录，避免写入用户文档目录造成打扰
        public static string DataDir = "";

        static string _exeDir;

        public static string ExeDir
        {
            get
            {
                if (_exeDir == null)
                {
                    try
                    {
                        _exeDir = Path.GetDirectoryName(
                            System.Reflection.Assembly.GetExecutingAssembly().Location);
                    }
                    catch { _exeDir = "."; }
                    if (string.IsNullOrEmpty(_exeDir)) _exeDir = ".";
                }
                return _exeDir;
            }
        }

        public static string ConfigFile { get { return Path.Combine(ExeDir, "config.txt"); } }

        public static void Load()
        {
            // 默认值
            DataDir = Path.Combine(ExeDir, "data");

            // config.txt（key=value，每行一条，# 开头为注释）
            try
            {
                if (File.Exists(ConfigFile))
                {
                    foreach (string raw in File.ReadAllLines(ConfigFile, Encoding.UTF8))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string v = line.Substring(eq + 1).Trim().Trim('"');
                        if (v.Length == 0) continue;

                        if (k == "datadir" || k == "data_dir") DataDir = v;
                        else if (k == "ollama" || k == "ollama_base") OllamaBase = v;
                    }
                }
            }
            catch { }

            // 环境变量优先（便于临时覆盖与自动化测试）
            try
            {
                string d = Environment.GetEnvironmentVariable("LITREADER_DATA_DIR");
                if (!string.IsNullOrEmpty(d)) DataDir = d;
                string o = Environment.GetEnvironmentVariable("LITREADER_OLLAMA");
                if (!string.IsNullOrEmpty(o)) OllamaBase = o;
            }
            catch { }
        }

        // 首次运行时把当前生效配置写出来，方便用户修改
        public static void SaveIfMissing()
        {
            try
            {
                if (File.Exists(ConfigFile)) return;
                var sb = new StringBuilder();
                sb.AppendLine("# LitReader 配置（修改后重启程序生效）");
                sb.AppendLine("#");
                sb.AppendLine("# 数据目录：长期记忆、学术词汇、术语本都存放在这里");
                sb.AppendLine("DataDir=" + DataDir);
                sb.AppendLine("#");
                sb.AppendLine("# Ollama 服务地址");
                sb.AppendLine("OllamaBase=" + OllamaBase);
                File.WriteAllText(ConfigFile, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        public static void PersistDataDir(string dir)
        {
            DataDir = dir;
            try
            {
                bool found = false;
                var lines = new System.Collections.Generic.List<string>();
                if (File.Exists(ConfigFile))
                {
                    foreach (string raw in File.ReadAllLines(ConfigFile, Encoding.UTF8))
                    {
                        if (raw.TrimStart().StartsWith("DataDir=", StringComparison.OrdinalIgnoreCase))
                        {
                            lines.Add("DataDir=" + dir);
                            found = true;
                        }
                        else lines.Add(raw);
                    }
                }
                if (!found) lines.Add("DataDir=" + dir);
                File.WriteAllLines(ConfigFile, lines.ToArray(), Encoding.UTF8);
            }
            catch { }
        }
    }
}
