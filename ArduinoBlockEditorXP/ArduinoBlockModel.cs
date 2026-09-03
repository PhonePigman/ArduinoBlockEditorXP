using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ArduinoBlockEditor
{
    public class LoadResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// データ構造の保持、ファイルの入出力、Undo/Redo、コード生成を行うロジッククラス
    /// </summary>
    public class ArduinoBlockModel
    {
        public static readonly List<string> DefaultCategoryOrder = new List<string>
        {
            "入出力・時間",
            "制御構文",
            "演算・計算",
            "変数・代入",
            "シリアル通信",
            "その他"
        };

        public List<JToken> BlockConfigs { get; private set; }
        public List<string> Categories { get; private set; }
        public string CurrentFilePath { get; set; }

        public List<JToken> SetupBlocks { get; set; }
        public List<JToken> LoopBlocks { get; set; }
        public List<JToken> FuncBlocks { get; set; }

        private readonly List<string> undoStack = new List<string>();
        private readonly List<string> redoStack = new List<string>();
        private readonly int maxHistory = 50;
        private bool isUndoRedoAction = false;

        private string BaseDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        public ArduinoBlockModel()
        {
            BlockConfigs = new List<JToken>();
            Categories = new List<string> { "すべて" };
            CurrentFilePath = null;

            SetupBlocks = new List<JToken>();
            LoopBlocks = new List<JToken>();
            FuncBlocks = new List<JToken>();
        }

        /// <summary>
        /// mods / mod フォルダからJSON設定ファイルを読み込む
        /// </summary>
        public LoadResult LoadMods()
        {
            BlockConfigs.Clear();
            var targetDirs = new[]
            {
                Path.Combine(BaseDir, "mods"),
                Path.Combine(BaseDir, "mod")
            };

            var foundFiles = new List<string>();
            foreach (var d in targetDirs)
            {
                if (Directory.Exists(d))
                {
                    foundFiles.AddRange(Directory.GetFiles(d, "*.json"));
                }
            }

            if (foundFiles.Count == 0)
            {
                return new LoadResult
                {
                    Success = false,
                    Message = string.Format("『{0}』 の中に 'mods' フォルダを作成し、JSONを置いてください。", BaseDir)
                };
            }

            var encodings = new[] { Encoding.UTF8, Encoding.GetEncoding("shift_jis") };

            foreach (var filePath in foundFiles)
            {
                JToken data = null;
                foreach (var enc in encodings)
                {
                    try
                    {
                        var jsonText = File.ReadAllText(filePath, enc);
                        data = JToken.Parse(jsonText);
                        if (data != null) break;
                    }
                    catch
                    {
                        // 読み込み失敗時はエンコーディングを変えて再試行
                    }
                }

                if (data != null && data["blocks"] is JArray)
                {
                    JArray blocksArray = (JArray)data["blocks"];
                    foreach (var b in blocksArray)
                    {
                        if (b != null)
                        {
                            BlockConfigs.Add(b.DeepClone());
                        }
                    }
                }
            }

            var cats = new HashSet<string>();
            foreach (var b in BlockConfigs)
            {
                var cat = b["category"] != null ? b["category"].ToString() : "その他";
                cats.Add(cat);
            }
            cats.Add("変数・代入");

            var sortedCats = cats.OrderBy(c =>
            {
                int idx = DefaultCategoryOrder.IndexOf(c);
                return idx >= 0 ? idx : DefaultCategoryOrder.Count;
            }).ThenBy(c => c).ToList();

            Categories = new List<string> { "すべて" };
            Categories.AddRange(sortedCats);

            return new LoadResult { Success = true, Message = string.Empty };
        }

        /// <summary>
        /// 現在のブロック配置状態をUndoスタックに保存
        /// </summary>
        public void SaveState()
        {
            if (isUndoRedoAction) return;

            var stateNode = new JObject
            {
                ["setup_blocks"] = JToken.FromObject(SetupBlocks),
                ["loop_blocks"] = JToken.FromObject(LoopBlocks),
                ["func_blocks"] = JToken.FromObject(FuncBlocks)
            };

            string stateJson = stateNode.ToString(Formatting.None);

            if (undoStack.Count > 0 && undoStack.Last() == stateJson)
            {
                return;
            }

            undoStack.Add(stateJson);
            if (undoStack.Count > maxHistory)
            {
                undoStack.RemoveAt(0);
            }

            redoStack.Clear();
        }

        /// <summary>
        /// アンドゥ処理
        /// </summary>
        public bool Undo()
        {
            if (undoStack.Count > 1)
            {
                isUndoRedoAction = true;
                string currentState = undoStack.Last();
                undoStack.RemoveAt(undoStack.Count - 1);
                redoStack.Add(currentState);

                string prevStateJson = undoStack.Last();
                RestoreStateFromJson(prevStateJson);

                isUndoRedoAction = false;
                return true;
            }
            return false;
        }

        /// <summary>
        /// リドゥ処理
        /// </summary>
        public bool Redo()
        {
            if (redoStack.Count > 0)
            {
                isUndoRedoAction = true;
                string nextStateJson = redoStack.Last();
                redoStack.RemoveAt(redoStack.Count - 1);
                undoStack.Add(nextStateJson);

                RestoreStateFromJson(nextStateJson);

                isUndoRedoAction = false;
                return true;
            }
            return false;
        }

        private void RestoreStateFromJson(string jsonStr)
        {
            var node = JToken.Parse(jsonStr);
            SetupBlocks = node["setup_blocks"] != null ? node["setup_blocks"].ToObject<List<JToken>>() : new List<JToken>();
            LoopBlocks = node["loop_blocks"] != null ? node["loop_blocks"].ToObject<List<JToken>>() : new List<JToken>();
            FuncBlocks = node["func_blocks"] != null ? node["func_blocks"].ToObject<List<JToken>>() : new List<JToken>();
        }

        /// <summary>
        /// クリア処理
        /// </summary>
        public void ClearAll()
        {
            SetupBlocks.Clear();
            LoopBlocks.Clear();
            FuncBlocks.Clear();
            CurrentFilePath = null;
        }

        /// <summary>
        /// ブロック設定からデフォルト値を持つインスタンスデータを生成
        /// </summary>
        public JObject CreateDefaultBlockData(JToken config)
        {
            var values = new JObject();
            if (config["params"] is JArray)
            {
                JArray paramsArr = (JArray)config["params"];
                foreach (var p in paramsArr)
                {
                    string key = p["key"] != null ? p["key"].ToString() : null;
                    if (!string.IsNullOrEmpty(key))
                    {
                        values[key] = p["default"] != null ? p["default"].ToString() : "";
                    }
                }
            }

            return new JObject
            {
                ["config"] = config.DeepClone(),
                ["values"] = values
            };
        }

        /// <summary>
        /// 宣言されている変数名の一覧を取得
        /// </summary>
        public List<string> GetDeclaredVariables()
        {
            var varsList = new List<string>();
            var allBlocks = SetupBlocks.Concat(LoopBlocks).Concat(FuncBlocks);

            Action<IEnumerable<JToken>> findVarDefs = null;
            findVarDefs = (blockList) =>
            {
                foreach (var b in blockList)
                {
                    if (b is JObject)
                    {
                        JObject obj = (JObject)b;
                        bool isVarDef = obj["config"] != null && obj["config"]["is_var_def"] != null ? obj["config"]["is_var_def"].Value<bool>() : false;
                        if (isVarDef)
                        {
                            string vname = obj["values"] != null && obj["values"]["name"] != null ? obj["values"]["name"].ToString().Trim() : "";
                            if (!string.IsNullOrEmpty(vname) && !varsList.Contains(vname))
                            {
                                varsList.Add(vname);
                            }
                        }

                        if (obj["values"] is JObject)
                        {
                            JObject valObj = (JObject)obj["values"];
                            foreach (var kvp in valObj)
                            {
                                if (kvp.Value is JObject)
                                {
                                    findVarDefs(new[] { (JObject)kvp.Value });
                                }
                            }
                        }
                    }
                }
            };

            findVarDefs(allBlocks);
            return varsList;
        }

        /// <summary>
        /// プロジェクトファイルの読み込み
        /// </summary>
        public bool LoadProjectFile(string filePath)
        {
            var text = File.ReadAllText(filePath, Encoding.UTF8);
            var data = JToken.Parse(text);

            if (data["setup_blocks"] != null && data["loop_blocks"] != null)
            {
                SetupBlocks = data["setup_blocks"].ToObject<List<JToken>>() ?? new List<JToken>();
                LoopBlocks = data["loop_blocks"].ToObject<List<JToken>>() ?? new List<JToken>();
                FuncBlocks = data["func_blocks"] != null ? data["func_blocks"].ToObject<List<JToken>>() : new List<JToken>();
                CurrentFilePath = filePath;
                return true;
            }
            return false;
        }

        /// <summary>
        /// プロジェクトファイルの保存
        /// </summary>
        public void SaveProjectFile(string filePath)
        {
            var data = new JObject
            {
                ["setup_blocks"] = JToken.FromObject(SetupBlocks),
                ["loop_blocks"] = JToken.FromObject(LoopBlocks),
                ["func_blocks"] = JToken.FromObject(FuncBlocks)
            };

            File.WriteAllText(filePath, data.ToString(Formatting.Indented), Encoding.UTF8);
            CurrentFilePath = filePath;
        }

        /// <summary>
        /// 単一ブロックからコード文字列を再帰的に生成
        /// </summary>
        public string EvaluateBlockCode(JToken block)
        {
            string template = block["config"] != null && block["config"]["template"] != null ? block["config"]["template"].ToString() : "";
            if (block["values"] is JObject)
            {
                JObject values = (JObject)block["values"];
                foreach (var kvp in values)
                {
                    string k = kvp.Key;
                    var v = kvp.Value;

                    if (v is JObject)
                    {
                        string subCode = EvaluateBlockCode((JObject)v);
                        template = template.Replace("{{" + k + "}}", subCode);
                    }
                    else
                    {
                        template = template.Replace("{{" + k + "}}", v != null ? v.ToString() : "");
                    }
                }
            }
            return template;
        }

        /// <summary>
        /// 全体からArduino用C++コードを生成
        /// </summary>
        public string GenerateArduinoCode()
        {
            var codeLines = new List<string>();

            if (FuncBlocks.Count > 0)
            {
                int indentLevel = 0;
                foreach (var block in FuncBlocks)
                {
                    string line = EvaluateBlockCode(block);
                    if (line.StartsWith("}"))
                    {
                        indentLevel = Math.Max(0, indentLevel - 1);
                    }
                    codeLines.Add(new string(' ', indentLevel * 2) + line);
                    if (line.EndsWith("{"))
                    {
                        indentLevel++;
                    }
                }
                codeLines.Add("\n");
            }

            codeLines.Add("void setup() {");
            int setupIndent = 1;
            foreach (var block in SetupBlocks)
            {
                string line = EvaluateBlockCode(block);
                if (line.StartsWith("}"))
                {
                    setupIndent = Math.Max(1, setupIndent - 1);
                }
                codeLines.Add(new string(' ', setupIndent * 2) + line);
                if (line.EndsWith("{"))
                {
                    setupIndent++;
                }
            }
            codeLines.Add("}\n");

            codeLines.Add("void loop() {");
            int loopIndent = 1;
            foreach (var block in LoopBlocks)
            {
                string line = EvaluateBlockCode(block);
                if (line.StartsWith("}"))
                {
                    loopIndent = Math.Max(1, loopIndent - 1);
                }
                codeLines.Add(new string(' ', loopIndent * 2) + line);
                if (line.EndsWith("{"))
                {
                    loopIndent++;
                }
            }
            codeLines.Add("}");

            return string.Join("\n", codeLines.ToArray());
        }
    }
}