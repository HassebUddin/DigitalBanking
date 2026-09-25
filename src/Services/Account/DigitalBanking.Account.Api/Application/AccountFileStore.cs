using DigitalBanking.BuildingBlocks.Exceptions;

namespace DigitalBanking.Account.Api.Application;

public sealed class AccountFileStore(IWebHostEnvironment environment)
{
    public async Task<string> SaveAsync(IFormFile file, string folder, CancellationToken cancellationToken)
    {
        if (file is null || file.Length <= 0 || file.Length > 8 * 1024 * 1024)
        {
            throw new ValidationException("Each document must be between 1 byte and 8 MB.");
        }

        var uploadsFolder = Path.Combine(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"), "uploads", folder);
        Directory.CreateDirectory(uploadsFolder);
        var storedName = $"{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}";
        await using var stream = File.Create(Path.Combine(uploadsFolder, storedName));
        await file.CopyToAsync(stream, cancellationToken);
        return $"/uploads/{folder}/{storedName}";
    }
}
