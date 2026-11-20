using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace XRStudy.Task1.Editor
{
    /// <summary>
    /// Stamps every player build with the exact Editor, package and app versions (spec section 2: freeze and
    /// record versions). Each session copies this into session_metadata.json → buildInfo.
    /// </summary>
    public class Task1BuildInfoPreprocessor : IPreprocessBuildWithReport
    {
        const string Dir = "Assets/Scripts/Task1/Resources";
        const string PathJson = Dir + "/Task1BuildInfo.json";

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(PathJson, Generate(report.summary.platform.ToString()), new UTF8Encoding(false));
            AssetDatabase.ImportAsset(PathJson);
        }

        [MenuItem("XR Study/Task 1/Write Build Info Now")]
        static void WriteNow()
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(PathJson, Generate(EditorUserBuildSettings.activeBuildTarget.ToString()), new UTF8Encoding(false));
            AssetDatabase.ImportAsset(PathJson);
            Debug.Log("[Task1] Wrote " + PathJson);
        }

        static string Generate(string target)
        {
            var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                .OrderBy(p => p.name)
                .Select(p => $"\"{p.name}\": \"{p.version}\"");
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"builtUtc\": \"{DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)}\",");
            sb.AppendLine($"  \"unityVersion\": \"{Application.unityVersion}\",");
            sb.AppendLine($"  \"appVersion\": \"{PlayerSettings.bundleVersion}\",");
            sb.AppendLine($"  \"androidBundleVersionCode\": {PlayerSettings.Android.bundleVersionCode},");
            sb.AppendLine($"  \"applicationIdentifier\": \"{PlayerSettings.applicationIdentifier}\",");
            sb.AppendLine($"  \"buildTarget\": \"{target}\",");
            sb.AppendLine("  \"packages\": {");
            sb.AppendLine("    " + string.Join(",\n    ", packages));
            sb.AppendLine("  }");
            sb.AppendLine("}");
            return sb.ToString();
        }
    }
}
