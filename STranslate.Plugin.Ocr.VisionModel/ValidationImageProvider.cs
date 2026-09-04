using System.IO;
using System.Reflection;

namespace STranslate.Plugin.Ocr.VisionModel;

/// <summary>
/// 读取随插件发布的验证图片。
/// 使用程序集定位资源，避免依赖当前工作目录或宿主安装目录的固定路径。
/// </summary>
internal static class ValidationImageProvider
{
    private const string RelativeImagePath = "Assets\\validation-image.png";
    private const string ResourceNameSuffix = "Assets.validation-image.png";

    public static byte[] Load()
    {
        var assembly = typeof(ValidationImageProvider).Assembly;
        var checkedPaths = new List<string>();

        // STranslate 将每个插件解压到独立目录，插件 DLL 所在目录是运行时最可靠的资源根目录。
        var assemblyLocation = assembly.Location;
        if (!string.IsNullOrWhiteSpace(assemblyLocation))
        {
            var assemblyDirectory = Path.GetDirectoryName(assemblyLocation);
            if (!string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                var pluginPath = Path.Combine(assemblyDirectory, RelativeImagePath);
                checkedPaths.Add(pluginPath);
                var pluginBytes = TryReadImageFile(pluginPath);
                if (pluginBytes is not null) return pluginBytes;
            }
        }

        // 调试加载器可能把插件程序集放在临时目录，此处保留宿主基目录作为兼容候选位置。
        var basePath = Path.Combine(AppContext.BaseDirectory, RelativeImagePath);
        if (!checkedPaths.Contains(basePath, StringComparer.OrdinalIgnoreCase))
        {
            checkedPaths.Add(basePath);
            var baseBytes = TryReadImageFile(basePath);
            if (baseBytes is not null) return baseBytes;
        }

        // 某些加载器不提供程序集物理路径，嵌入资源保证验证流程不依赖当前工作目录。
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(ResourceNameSuffix, StringComparison.OrdinalIgnoreCase));
        if (resourceName is not null)
        {
            using var resourceStream = assembly.GetManifestResourceStream(resourceName);
            if (resourceStream is not null)
            {
                using var memoryStream = new MemoryStream();
                resourceStream.CopyTo(memoryStream);
                var resourceBytes = memoryStream.ToArray();
                if (resourceBytes.Length == 0)
                    throw new InvalidDataException($"插件内置验证图片资源为空，资源名：{resourceName}。");
                return resourceBytes;
            }
        }

        throw new FileNotFoundException(
            $"插件内置验证图片不存在。已检查路径：{string.Join("；", checkedPaths)}。程序集资源名：{ResourceNameSuffix}。",
            checkedPaths.FirstOrDefault() ?? RelativeImagePath);
    }

    private static byte[]? TryReadImageFile(string path)
    {
        if (!File.Exists(path)) return null;

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0)
            throw new InvalidDataException($"插件内置验证图片为空：{path}。");

        return bytes;
    }
}
