using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ArduinoBlockEditorXP
{
    public class UserFunction
    {
        public string Name { get; set; } = "";
        public List<JToken> ParametersBlocks { get; set; } = new List<JToken>();
        public List<JToken> Blocks { get; set; } = new List<JToken>();
    }

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
            "関数",
            "シリアル通信",
            "その他"
        };

        public List<JToken> BlockConfigs { get; private set; }
        public List<string> Categories { get; private set; }
        public string CurrentFilePath { get; set; }

        public List<JToken> SetupBlocks { get; set; }
        public List<JToken> LoopBlocks { get; set; }
        public List<JToken> FuncBlocks { get; set; }
        public List<UserFunction> UserFunctions { get; set; }

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
            UserFunctions = new List<UserFunction>();
        }

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
                    catch { }
                }

                if (data != null && data["blocks"] is JArray blocksArray)
                {
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
            cats.Add("関数");

            var sortedCats = cats.OrderBy(c =>
            {
                int idx = DefaultCategoryOrder.IndexOf(c);
                return idx >= 0 ? idx : DefaultCategoryOrder.Count;
            }).ThenBy(c => c).ToList();

            Categories = new List<string> { "すべて" };
            Categories.AddRange(sortedCats);

            return new LoadResult { Success = true, Message = string.Empty };
        }

        public bool AddUserFunction(string funcName)
        {
            if (string.IsNullOrEmpty(funcName)) return false;
            funcName = funcName.Trim();
            if (UserFunctions.Any(f => f.Name == funcName)) return false;

            UserFunctions.Add(new UserFunction { Name = funcName });
            return true;
        }

        public void RemoveUserFunction(string funcName)
        {
            UserFunctions.RemoveAll(f => f.Name == funcName);
        }

        public bool RenameUserFunction(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(newName)) return false;
            newName = newName.Trim();
            if (oldName == newName) return true;
            if (UserFunctions.Any(f => f.Name == newName)) return false;

            var target = UserFunctions.FirstOrDefault(f => f.Name == oldName);
            if (target != null)
            {
                target.Name = newName;
                return true;
            }
            return false;
        }

        public void SaveState()
        {
            if (isUndoRedoAction) return;

            var stateNode = new JObject
            {
                ["setup_blocks"] = JToken.FromObject(SetupBlocks),
                ["loop_blocks"] = JToken.FromObject(LoopBlocks),
                ["func_blocks"] = JToken.FromObject(FuncBlocks),
                ["user_functions"] = JToken.FromObject(UserFunctions)
            };

            string stateJson = stateNode.ToString(Formatting.None);

            if (undoStack.Count > 0 && undoStack.Last() == stateJson) return;

            undoStack.Add(stateJson);
            if (undoStack.Count > maxHistory) undoStack.RemoveAt(0);

            redoStack.Clear();
        }

        public bool Undo()
        {
            if (undoStack.Count > 1)
            {
                isUndoRedoAction = true;
                string currentState = undoStack.Last();
                undoStack.RemoveAt(undoStack.Count - 1);
                redoStack.Add(currentState);

                RestoreStateFromJson(undoStack.Last());
                isUndoRedoAction = false;
                return true;
            }
            return false;
        }

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
            UserFunctions = node["user_functions"] != null ? node["user_functions"].ToObject<List<UserFunction>>() : new List<UserFunction>();
        }

        public void ClearAll()
        {
            SetupBlocks.Clear();
            LoopBlocks.Clear();
            FuncBlocks.Clear();
            UserFunctions.Clear();
            CurrentFilePath = null;
        }

        public JObject CreateDefaultBlockData(JToken config)
        {
            var values = new JObject();
            if (config["params"] is JArray paramsArr)
            {
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

        public List<string> GetDeclaredVariables()
        {
            var varsList = new List<string>();
            var allBlocks = SetupBlocks.Concat(LoopBlocks).Concat(FuncBlocks);
            foreach (var uf in UserFunctions)
            {
                allBlocks = allBlocks.Concat(uf.ParametersBlocks).Concat(uf.Blocks);
            }

            Action<IEnumerable<JToken>> findVarDefs = null;
            findVarDefs = (blockList) =>
            {
                foreach (var b in blockList)
                {
                    if (b is JObject obj)
                    {
                        bool isVarDef = obj["config"] != null && obj["config"]["is_var_def"] != null ? obj["config"]["is_var_def"].Value<bool>() : false;
                        if (isVarDef)
                        {
                            string vname = obj["values"] != null && obj["values"]["name"] != null ? obj["values"]["name"].ToString().Trim() : "";
                            if (!string.IsNullOrEmpty(vname) && !varsList.Contains(vname))
                            {
                                varsList.Add(vname);
                            }
                        }

                        if (obj["values"] is JObject valObj)
                        {
                            foreach (var kvp in valObj)
                            {
                                if (kvp.Value is JObject subObj)
                                {
                                    findVarDefs(new[] { subObj });
                                }
                            }
                        }
                    }
                }
            };

            findVarDefs(allBlocks);
            return varsList;
        }

        public bool LoadProjectFile(string filePath)
        {
            var text = File.ReadAllText(filePath, Encoding.UTF8);
            var data = JToken.Parse(text);

            if (data["setup_blocks"] != null && data["loop_blocks"] != null)
            {
                SetupBlocks = data["setup_blocks"].ToObject<List<JToken>>() ?? new List<JToken>();
                LoopBlocks = data["loop_blocks"].ToObject<List<JToken>>() ?? new List<JToken>();
                FuncBlocks = data["func_blocks"] != null ? data["func_blocks"].ToObject<List<JToken>>() : new List<JToken>();
                UserFunctions = data["user_functions"] != null ? data["user_functions"].ToObject<List<UserFunction>>() : new List<UserFunction>();
                CurrentFilePath = filePath;
                return true;
            }
            return false;
        }

        public void SaveProjectFile(string filePath)
        {
            var data = new JObject
            {
                ["setup_blocks"] = JToken.FromObject(SetupBlocks),
                ["loop_blocks"] = JToken.FromObject(LoopBlocks),
                ["func_blocks"] = JToken.FromObject(FuncBlocks),
                ["user_functions"] = JToken.FromObject(UserFunctions)
            };

            File.WriteAllText(filePath, data.ToString(Formatting.Indented), Encoding.UTF8);
            CurrentFilePath = filePath;
        }

        public string EvaluateBlockCode(JToken block)
        {
            string template = block["config"] != null && block["config"]["template"] != null ? block["config"]["template"].ToString() : "";
            if (block["values"] is JObject values)
            {
                foreach (var kvp in values)
                {
                    string k = kvp.Key;
                    var v = kvp.Value;

                    if (v is JObject subBlock)
                    {
                        string subCode = EvaluateBlockCode(subBlock);
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

        public string GenerateArduinoCode()
        {
            var codeLines = new List<string>();

            if (FuncBlocks.Count > 0)
            {
                int indentLevel = 0;
                foreach (var block in FuncBlocks)
                {
                    string line = EvaluateBlockCode(block);
                    if (line.StartsWith("}")) indentLevel = Math.Max(0, indentLevel - 1);
                    codeLines.Add(new string(' ', indentLevel * 2) + line);
                    if (line.EndsWith("{")) indentLevel++;
                }
                codeLines.Add("\n");
            }

            codeLines.Add("void setup() {");
            int setupIndent = 1;
            foreach (var block in SetupBlocks)
            {
                string line = EvaluateBlockCode(block);
                if (line.StartsWith("}")) setupIndent = Math.Max(1, setupIndent - 1);
                codeLines.Add(new string(' ', setupIndent * 2) + line);
                if (line.EndsWith("{")) setupIndent++;
            }
            codeLines.Add("}\n");

            codeLines.Add("void loop() {");
            int loopIndent = 1;
            foreach (var block in LoopBlocks)
            {
                string line = EvaluateBlockCode(block);
                if (line.StartsWith("}")) loopIndent = Math.Max(1, loopIndent - 1);
                codeLines.Add(new string(' ', loopIndent * 2) + line);
                if (line.EndsWith("{")) loopIndent++;
            }
            codeLines.Add("}\n");

            foreach (var func in UserFunctions)
            {
                var paramCodes = new List<string>();
                foreach (var pBlock in func.ParametersBlocks)
                {
                    string pCode = EvaluateBlockCode(pBlock).Trim();
                    if (pCode.EndsWith(";")) pCode = pCode.Substring(0, pCode.Length - 1);
                    if (!string.IsNullOrEmpty(pCode)) paramCodes.Add(pCode);
                }
                string paramsStr = string.Join(", ", paramCodes.ToArray());

                codeLines.Add(string.Format("void {0}({1}) {{", func.Name, paramsStr));

                int indentLevel = 1;
                foreach (var block in func.Blocks)
                {
                    string line = EvaluateBlockCode(block);
                    if (line.StartsWith("}")) indentLevel = Math.Max(1, indentLevel - 1);
                    codeLines.Add(new string(' ', indentLevel * 2) + line);
                    if (line.EndsWith("{")) indentLevel++;
                }
                codeLines.Add("}\n");
            }

            return string.Join("\n", codeLines.ToArray()).TrimEnd();
        }
    }
}