using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;


namespace STranslate.Plugin.Ocr.VisionModel;

/// <summary>
/// 提供图片格式识别和原始字节摘要功能。
/// 该类对视觉模型支持的格式保留原始字节，对宿主生成的 BMP 使用无损 PNG 编码，避免服务端拒绝 BMP 格式。
/// </summary>
internal static class ImageDataHelper
{
    /// <summary>
    /// 将宿主传入的图片整理为视觉模型能够直接接受的格式。
    /// PNG、JPEG、GIF 和 WebP 保留原始字节，BMP 使用无损 PNG 编码器转换。
    /// </summary>
    public static ImageNormalizationResult NormalizeForVisionModel(byte[] data)
    {
        if (data is null || data.Length == 0)
            throw new InvalidDataException("OCR 图片字节为空，无法发送给视觉模型。");

        var originalMimeType = DetectMimeType(data);
        if (originalMimeType is "image/png" or "image/jpeg" or "image/gif" or "image/webp")
            return new ImageNormalizationResult(data, originalMimeType, originalMimeType, false);

        if (originalMimeType == "image/bmp")
            return ConvertBmpToPng(data);

        var signatureLength = Math.Min(data.Length, 16);
        var signature = Convert.ToHexString(data.AsSpan(0, signatureLength));
        throw new InvalidDataException(
            $"OCR 图片格式无法识别，原始 MIME 类型为 {originalMimeType}，字节数为 {data.Length}，文件头为 {signature}。"
            + "视觉模型请求已停止，请提供 PNG、JPEG、GIF、WebP 或 BMP 图片。");
    }

    /// <summary>
    /// 使用 WPF 的 BMP 解码器和 PNG 编码器进行无损格式转换。
    /// OnLoad 确保输出完成后不再依赖输入流，像素尺寸和内容保持不变。
    /// </summary>
    private static ImageNormalizationResult ConvertBmpToPng(byte[] data)
    {
        try
        {
            using var input = new MemoryStream(data, writable: false);
            var decoder = new BmpBitmapDecoder(
                input,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0)
                throw new InvalidDataException("BMP 图片没有可读取的图像帧。");

            var frame = decoder.Frames[0];
            if (frame.CanFreeze)
                frame.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using var output = new MemoryStream();
            encoder.Save(output);
            var pngBytes = output.ToArray();
            if (pngBytes.Length == 0)
                throw new InvalidDataException("BMP 无损转换后的 PNG 图片为空。");

            return new ImageNormalizationResult(data, "image/bmp", "image/png", true, pngBytes);
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException(
                $"BMP 图片无法无损转换为 PNG，原始字节数为 {data.Length}，异常信息：{ex.Message}。",
                ex);
        }
    }

    public static string DetectMimeType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (data.Length >= 3 && data[..3].SequenceEqual(new byte[] { 0xFF, 0xD8, 0xFF })) return "image/jpeg";
        if (data.Length >= 6 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8))) return "image/gif";
        if (data.Length >= 2 && data[..2].SequenceEqual("BM"u8)) return "image/bmp";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        return "application/octet-stream";
    }

    public static string ComputeSha256(ReadOnlySpan<byte> data)
    {
        return Convert.ToHexString(SHA256.HashData(data));
    }
}

/// <summary>
/// 保存图片标准化前后的字节和 MIME 信息，供请求构造及诊断日志共同使用。
/// </summary>
internal sealed record ImageNormalizationResult(
    byte[] OriginalData,
    string OriginalMimeType,
    string MimeType,
    bool Converted,
    byte[]? NormalizedData = null)
{
    public byte[] Data => NormalizedData ?? OriginalData;
}
