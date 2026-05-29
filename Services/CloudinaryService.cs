using CloudinaryDotNet;
using CloudinaryDotNet.Actions;

namespace QuizApi.Services;

/// <summary>
/// Upload ảnh lên Cloudinary và trả về URL public.
/// </summary>
public class CloudinaryService
{
    private readonly Cloudinary _cloudinary;

    public CloudinaryService(IConfiguration config)
    {
        var cloudName   = Environment.GetEnvironmentVariable("CLOUDINARY_CLOUD_NAME") ?? config["Cloudinary:CloudName"] ?? "";
        var apiKey      = Environment.GetEnvironmentVariable("CLOUDINARY_API_KEY")    ?? config["Cloudinary:ApiKey"]    ?? "";
        var apiSecret   = Environment.GetEnvironmentVariable("CLOUDINARY_API_SECRET") ?? config["Cloudinary:ApiSecret"] ?? "";

        var account = new Account(cloudName, apiKey, apiSecret);
        _cloudinary = new Cloudinary(account) { Api = { Secure = true } };
    }

    /// <summary>
    /// Upload byte[] ảnh lên Cloudinary, trả về URL CDN (https://).
    /// </summary>
    /// <param name="imageBytes">Dữ liệu ảnh (PNG/JPG...)</param>
    /// <param name="folder">Thư mục lưu trên Cloudinary, ví dụ "question-images"</param>
    /// <param name="publicId">ID của file (không có extension). Nếu null thì Cloudinary tự tạo.</param>
    public async Task<string> UploadImageAsync(byte[] imageBytes, string folder = "question-images", string? publicId = null)
    {
        using var stream = new MemoryStream(imageBytes);
        var uploadParams = new ImageUploadParams
        {
            File       = new FileDescription(publicId ?? Guid.NewGuid().ToString(), stream),
            Folder     = folder,
            PublicId   = publicId,
            Overwrite  = false,
            // Tối ưu tự động
            Transformation = new Transformation().Quality("auto").FetchFormat("auto")
        };

        var result = await _cloudinary.UploadAsync(uploadParams);

        if (result.Error != null)
            throw new Exception($"Cloudinary upload error: {result.Error.Message}");

        return result.SecureUrl.ToString();
    }

    /// <summary>
    /// Upload từ base64 string trực tiếp.
    /// </summary>
    public async Task<string> UploadBase64Async(string base64Image, string folder = "question-images", string? publicId = null)
    {
        var bytes = Convert.FromBase64String(base64Image);
        return await UploadImageAsync(bytes, folder, publicId);
    }
}
