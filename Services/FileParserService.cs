using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using PDFtoImage;
using QuizApi.Models;
using SkiaSharp;
using Spire.Doc;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace QuizApi.Services
{
    // ─── Model trung gian: kết quả phân tích tài liệu kèm vị trí ảnh ─────────
    public class DocumentWithImages
    {
        /// <summary>
        /// Văn bản đầy đủ của tài liệu, với placeholder [IMG_1], [IMG_2],...
        /// được chèn đúng vị trí mà ảnh xuất hiện trong tài liệu gốc.
        /// </summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>
        /// Danh sách ảnh trích xuất từ tài liệu.
        /// Key = tên placeholder (e.g. "IMG_1"), Value = JPEG Base64 thuần của ảnh đó.
        /// </summary>
        public Dictionary<string, string> Images { get; set; } = new();
    }

    public class FileParserService
    {
        private const int MaxTextLength = 40000;

        // ─── Cài đặt Vision (render toàn trang) ──────────────────────────────
        private const int RenderDpi = 150;
        private const int JpegQuality = 75;
        private const int MaxImageWidth = 1400;

        // ─── Cài đặt lưu ảnh ─────────────────────────────────────────────────
        // Kích thước ảnh tối thiểu để lọc bỏ icon/ảnh trang trí nhỏ (pixels)
        private const int MinImageDimension = 80;

        // ═══════════════════════════════════════════════════════════════════════
        //  NHÓM 1: TEXT-ONLY (backward compatible với ChatbotController)
        // ═══════════════════════════════════════════════════════════════════════

        public Task<string> ExtractTextFromPdfAsync(Stream stream)
        {
            var sb = new StringBuilder();
            try
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                ms.Position = 0;
                using var document = PdfDocument.Open(ms);
                foreach (var page in document.GetPages())
                {
                    sb.AppendLine(page.Text);
                    if (sb.Length > MaxTextLength) break;
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Không thể đọc tệp PDF: {ex.Message}", ex);
            }
            var text = sb.ToString().Trim();
            if (text.Length > MaxTextLength)
                text = text.Substring(0, MaxTextLength) + "\n...[Nội dung bị cắt bớt]";
            return Task.FromResult(text);
        }

        public Task<string> ExtractTextFromDocxAsync(Stream stream)
        {
            var sb = new StringBuilder();
            try
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                ms.Position = 0;
                using var wordDoc = WordprocessingDocument.Open(ms, false);
                var body = wordDoc.MainDocumentPart?.Document?.Body;
                if (body != null)
                {
                    foreach (var para in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
                    {
                        var paraText = para.InnerText;
                        if (!string.IsNullOrWhiteSpace(paraText))
                            sb.AppendLine(paraText);
                        if (sb.Length > MaxTextLength) break;
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Không thể đọc tệp Word: {ex.Message}", ex);
            }
            var text = sb.ToString().Trim();
            if (text.Length > MaxTextLength)
                text = text.Substring(0, MaxTextLength) + "\n...[Nội dung bị cắt bớt]";
            return Task.FromResult(text);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  NHÓM 2: VISION / RENDER TOÀN TRANG (cho parse-vision endpoint)
        // ═══════════════════════════════════════════════════════════════════════

        public async Task<ParsedDocument> ExtractFromPdfAsync(Stream stream)
        {
            var result = new ParsedDocument();
            try
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                var pdfBytes = ms.ToArray();
                int pageCount = Conversion.GetPageCount(pdfBytes, password: string.Empty);
                for (int i = 0; i < pageCount; i++)
                {
                    var options = new RenderOptions(Dpi: RenderDpi);
                    using var bitmap = Conversion.ToImage(pdfBytes, string.Empty, i, options);
                    result.PageImages.Add(BitmapToJpegBase64(bitmap));
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Không thể xử lý PDF thành ảnh trang: {ex.Message}", ex);
            }
            return result;
        }

        public Task<ParsedDocument> ExtractFromDocxAsync(Stream stream)
        {
            var result = new ParsedDocument();
            try
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                ms.Position = 0;
                var doc = new Spire.Doc.Document();
                doc.LoadFromStream(ms, FileFormat.Docx);
                var images = doc.SaveToImages(Spire.Doc.Documents.ImageType.Bitmap);
                foreach (var img in images)
                {
                    using var imgStream = new MemoryStream();
                    img.Save(imgStream, System.Drawing.Imaging.ImageFormat.Png);
                    imgStream.Position = 0;
                    using var skBitmap = SKBitmap.Decode(imgStream);
                    if (skBitmap != null) result.PageImages.Add(BitmapToJpegBase64(skBitmap));
                    img.Dispose();
                }
                doc.Close();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Không thể xử lý Word thành ảnh trang: {ex.Message}", ex);
            }
            return Task.FromResult(result);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  NHÓM 3: SMART PARSE — Trích xuất TEXT + ẢNH RIÊNG LẺ kèm vị trí
        //  Dùng cho endpoint parse-vision-smart (lưu ảnh vào disk, gắn ImageUrl)
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Phân tích PDF: trích xuất text có chèn placeholder [IMG_X] đúng vị trí,
        /// đồng thời trích xuất byte array của từng ảnh embedded trong PDF.
        /// </summary>
        public async Task<DocumentWithImages> ExtractTextAndImagesFromPdfAsync(Stream stream)
        {
            var result = new DocumentWithImages();
            var sb = new StringBuilder();
            int imgIndex = 0;

            try
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                ms.Position = 0;

                using var document = PdfDocument.Open(ms);
                foreach (var page in document.GetPages())
                {
                    // Chèn text của trang
                    var pageText = page.Text?.Trim() ?? string.Empty;

                    // Lấy tất cả ảnh embedded trong trang này
                    var pageImages = new List<(double x, double y, byte[] bytes, string ext)>();
                    foreach (var img in page.GetImages())
                    {
                        try
                        {
                            byte[]? rawBytes = null;
                            string ext = "jpg";

                            if (img.TryGetPng(out var pngBytes))
                            {
                                rawBytes = pngBytes;
                                ext = "png";
                            }
                            else if (img.RawBytes.Length > 0)
                            {
                                rawBytes = img.RawBytes.ToArray();
                            }

                            if (rawBytes == null || rawBytes.Length < 100) continue;

                            // Lọc ảnh quá nhỏ (icon, trang trí)
                            if (img.Bounds.Width < MinImageDimension || img.Bounds.Height < MinImageDimension)
                                continue;

                            pageImages.Add((img.Bounds.Left, img.Bounds.Bottom, rawBytes, ext));
                        }
                        catch { /* bỏ qua ảnh lỗi */ }
                    }

                    // Gộp text + placeholder ảnh theo thứ tự xuất hiện trên trang
                    // (PDF tọa độ Y tăng từ dưới lên, nên dùng Bottom để so sánh)
                    if (pageImages.Count == 0)
                    {
                        sb.AppendLine(pageText);
                    }
                    else
                    {
                        // Sắp xếp ảnh theo vị trí Y (từ trên xuống dưới trên trang)
                        pageImages.Sort((a, b) => b.y.CompareTo(a.y));

                        // Chèn text và placeholder xen kẽ
                        sb.AppendLine(pageText);
                        foreach (var (x, y, bytes, ext) in pageImages)
                        {
                            imgIndex++;
                            var key = $"IMG_{imgIndex}";
                            sb.AppendLine($"[{key}]");

                            // Chuyển bytes → JPEG Base64
                            var base64 = ConvertImageBytesToJpegBase64(bytes, ext);
                            if (!string.IsNullOrEmpty(base64))
                                result.Images[key] = base64;
                        }
                    }

                    if (sb.Length > MaxTextLength) break;
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Không thể phân tích PDF: {ex.Message}", ex);
            }

            result.Text = sb.ToString().Trim();
            return result;
        }

        /// <summary>
        /// Phân tích Word (.docx): đọc từng đoạn văn và phần tử inline (text + ảnh),
        /// chèn placeholder [IMG_X] đúng vị trí ảnh xuất hiện trong document.
        /// </summary>
        public Task<DocumentWithImages> ExtractTextAndImagesFromDocxAsync(Stream stream)
        {
            var result = new DocumentWithImages();
            var sb = new StringBuilder();
            int imgIndex = 0;

            try
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                ms.Position = 0;

                using var wordDoc = WordprocessingDocument.Open(ms, false);
                var body = wordDoc.MainDocumentPart?.Document?.Body;
                if (body == null) return Task.FromResult(result);

                // Duyệt từng phần tử con trực tiếp của body (Paragraph hoặc Table)
                foreach (var element in body.ChildElements)
                {
                    if (element is DocumentFormat.OpenXml.Wordprocessing.Paragraph para)
                    {
                        bool hasImage = false;

                        // Kiểm tra paragraph có chứa ảnh không
                        foreach (var drawing in para.Descendants<DocumentFormat.OpenXml.Wordprocessing.Drawing>())
                        {
                            // Lấy relationship ID của ảnh
                            var blip = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Blip>().FirstOrDefault();
                            if (blip?.Embed?.Value == null) continue;

                            var rid = blip.Embed.Value;
                            if (wordDoc.MainDocumentPart?.GetPartById(rid) is ImagePart imagePart)
                            {
                                try
                                {
                                    using var imgStream = imagePart.GetStream();
                                    using var imgMs = new MemoryStream();
                                    imgStream.CopyTo(imgMs);
                                    var bytes = imgMs.ToArray();

                                    if (bytes.Length < 100) continue;

                                    imgIndex++;
                                    var key = $"IMG_{imgIndex}";

                                    var contentType = imagePart.ContentType; // e.g. "image/png"
                                    var ext = contentType.Contains("png") ? "png" : "jpg";

                                    var base64 = ConvertImageBytesToJpegBase64(bytes, ext);
                                    if (!string.IsNullOrEmpty(base64))
                                    {
                                        result.Images[key] = base64;
                                        sb.AppendLine($"[{key}]");
                                        hasImage = true;
                                    }
                                }
                                catch { /* bỏ qua ảnh lỗi */ }
                            }
                        }

                        // Thêm text của paragraph (nếu có)
                        if (!hasImage)
                        {
                            var paraText = para.InnerText;
                            if (!string.IsNullOrWhiteSpace(paraText))
                                sb.AppendLine(paraText);
                        }
                    }
                    else if (element is DocumentFormat.OpenXml.Wordprocessing.Table table)
                    {
                        // Bảng: đọc text từng ô
                        foreach (var row in table.Descendants<DocumentFormat.OpenXml.Wordprocessing.TableRow>())
                        {
                            var cells = new List<string>();
                            foreach (var cell in row.Descendants<DocumentFormat.OpenXml.Wordprocessing.TableCell>())
                                cells.Add(cell.InnerText);
                            if (cells.Count > 0)
                                sb.AppendLine(string.Join(" | ", cells));
                        }
                    }

                    if (sb.Length > MaxTextLength) break;
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Không thể phân tích Word: {ex.Message}", ex);
            }

            result.Text = sb.ToString().Trim();
            return Task.FromResult(result);
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private static string BitmapToJpegBase64(SKBitmap bitmap)
        {
            SKBitmap? scaled = null;
            var target = bitmap;
            if (bitmap.Width > MaxImageWidth)
            {
                var ratio = (float)MaxImageWidth / bitmap.Width;
                var h = (int)(bitmap.Height * ratio);
                scaled = bitmap.Resize(new SKImageInfo(MaxImageWidth, h), SKFilterQuality.High);
                target = scaled ?? bitmap;
            }
            try
            {
                using var image = SKImage.FromBitmap(target);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
                return Convert.ToBase64String(data.ToArray());
            }
            finally { scaled?.Dispose(); }
        }

        /// <summary>
        /// Chuyển raw bytes ảnh (PNG hoặc JPEG) thành JPEG Base64 thuần.
        /// Dùng SkiaSharp để decode → re-encode JPEG, đảm bảo định dạng nhất quán.
        /// </summary>
        private static string ConvertImageBytesToJpegBase64(byte[] bytes, string ext)
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                using var bitmap = SKBitmap.Decode(ms);
                if (bitmap == null) return string.Empty;

                // Lọc ảnh quá nhỏ sau khi decode
                if (bitmap.Width < MinImageDimension || bitmap.Height < MinImageDimension)
                    return string.Empty;

                return BitmapToJpegBase64(bitmap);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
