using DigitalBanking.BuildingBlocks.Exceptions;

namespace DigitalBanking.Chat.Api.Application;

public sealed class ChatFileStore(IWebHostEnvironment environment)
{
    public async Task<(string fileName, string fileUrl, string contentType)> SaveAsync(IFormFile file, string folder, CancellationToken cancellationToken)
    {
        if (file.Length == 0)
        {
            throw new ValidationException("The uploaded file is empty.");
        }

        if (file.Length > 20 * 1024 * 1024)
        {
            throw new ValidationException("Files larger than 20 MB are not allowed.");
        }

        var uploadsFolder = Path.Combine(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"), "uploads", folder);
        Directory.CreateDirectory(uploadsFolder);

        var extension = Path.GetExtension(file.FileName);
        var storedName = $"{Guid.NewGuid():N}{extension}";
        var fullPath = Path.Combine(uploadsFolder, storedName);

        await using var stream = File.Create(fullPath);
        await file.CopyToAsync(stream, cancellationToken);

        return (file.FileName, $"/uploads/{folder}/{storedName}", file.ContentType);
    }
}
