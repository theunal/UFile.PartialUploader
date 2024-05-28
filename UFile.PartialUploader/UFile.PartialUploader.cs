using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using System.Net;

namespace UFile.PartialUploader;

/// <summary>
/// Handles the upload of chunked files.
/// </summary>
/// <param name="Request">The HTTP request containing the chunked file data.</param>
/// <returns>The status code indicating the result of the operation.</returns>
public class UFileHelper
{
    public async Task<HttpStatusCode> UploadChunkFiles([NotNull] HttpRequest Request)
    {
        ArgumentNullException.ThrowIfNull(Request);

        var file = Request.Form.Files[0];
        var fileGuid = Request.Form["fileGuid"].ToString();
        var isDone = bool.Parse(Request.Form["isDone"].ToString());
        var totalSize = long.Parse(Request.Form["totalSize"].ToString());
        var totalChunks = int.Parse(Request.Form["totalChunks"].ToString());
        var filename = Request.Form["filename"].ToString();

        if (file is null || fileGuid is null || filename is null) return HttpStatusCode.NotFound;

        string chunk_files_folder_name = "__UFile.ChunkFiles__";
        var dirPath = Path.Combine(UFileStaticHelper.BasePath, chunk_files_folder_name, fileGuid);
        var save = await UFileStaticHelper.SaveWithRetryAsync(dirPath, file.FileName, file, 3, 3);
        if (save is false) return HttpStatusCode.BadRequest;

        if (isDone)
        {
            byte[] bytes = new byte[totalSize];
            using var ms = new MemoryStream();

            var chunkRes = await UFileStaticHelper.ChunkMerger(ms, totalChunks, dirPath, filename);
            if (chunkRes is false)
            {
                Directory.Delete(dirPath, true);
                return HttpStatusCode.NotAcceptable;
            }

            bytes = ms.ToArray();
            await ms.FlushAsync();
            ms.Close();

            var temp_file_path = Path.Combine(dirPath.Replace(chunk_files_folder_name, UFileStaticHelper.TempFolderName));
            await UFileStaticHelper.SaveFile(temp_file_path, filename, bytes);

            Directory.Delete(dirPath, true);
        }

        return HttpStatusCode.OK;
    }
}

public static class UFileStaticHelper
{
    public static string BasePath = null!;
    public static string TempFolderName = null!;

    /// <summary>
    /// Attempts to save a file with retries if the initial attempts fail.
    /// </summary>
    /// <param name="dirPath">The directory path where the file will be saved.</param>
    /// <param name="fileName">The name of the file to save.</param>
    /// <param name="file">The file to save.</param>
    /// <param name="retryCount">The number of retry attempts.</param>
    /// <param name="delaySeconds">The delay between retries in seconds.</param>
    /// <returns>A task that represents the asynchronous save operation. The task result contains a boolean indicating success or failure.</returns>
    public static async Task<bool> SaveWithRetryAsync(string dirPath, string fileName, IFormFile file, int retryCount, int delaySeconds)
    {
        bool result = false;
        int attempts = 0;

        Task<bool> save_file() => Task.Run(() => SaveFile(dirPath, fileName, file));

        while (attempts <= retryCount)
        {
            result = await save_file();
            if (result) return true;

            attempts++;

            if (attempts <= retryCount)
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
        }

        return result;
    }

    /// <summary>
    /// Merges chunk files into a single file.
    /// </summary>
    /// <param name="ms">The memory stream where the chunks will be merged.</param>
    /// <param name="totalChunks">The total number of chunks to merge.</param>
    /// <param name="dirPath">The directory path where the chunk files are located.</param>
    /// <param name="filename">The base filename of the chunks.</param>
    /// <returns>A task that represents the asynchronous merge operation. The task result contains a boolean indicating success or failure.</returns>
    public static async Task<bool> ChunkMerger(MemoryStream ms, int totalChunks, string dirPath, string filename)
    {
        for (var i = 0; i < totalChunks;)
        {
            i++;
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), dirPath, $"{filename}_chunk_{i}");
            if (File.Exists(filePath))
            {
                using var fileStream = new FileStream(filePath, FileMode.Open);
                await fileStream.CopyToAsync(ms);
                await fileStream.FlushAsync();
                fileStream.Close();
            }
            else
            {
                await ms.FlushAsync();
                ms.Close();

                // chunk files removed
                Directory.Delete(dirPath, true);

                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Saves a file from a byte array to the specified path.
    /// </summary>
    /// <param name="path">The directory path where the file will be saved.</param>
    /// <param name="filename">The name of the file to save.</param>
    /// <param name="bytes">The byte array representing the file content.</param>
    /// <returns>A task that represents the asynchronous save operation.</returns>
    public static async Task SaveFile(string path, string filename, byte[] bytes)
    {
        Directory.CreateDirectory(path);
        using var fs = new FileStream(Path.Combine(path, filename), FileMode.Create, FileAccess.Write);
        await fs.WriteAsync(bytes);
        await fs.FlushAsync();
        fs.Close();
    }

    /// <summary>
    /// Saves an uploaded file to the specified path.
    /// </summary>
    /// <param name="dirPath">The directory path where the file will be saved.</param>
    /// <param name="filename">The name of the file to save.</param>
    /// <param name="file">The uploaded file to save.</param>
    /// <returns>A boolean indicating success or failure.</returns>
    private static bool SaveFile(string dirPath, string filename, IFormFile file)
    {
        try
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), dirPath);
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);

            var filepath = Path.Combine(path, filename);
            using FileStream fileStream = File.Create(filepath);
            file.CopyTo(fileStream);
            fileStream.Flush();
            fileStream.Close();

            return File.Exists(filepath);
        }
        catch (Exception e)
        {
            Console.WriteLine($"SaveFile error. => {e.Source} {e.Message} {e.StackTrace}");
            return false;
        }
    }
}

/// <summary>
/// Adds the UFile service to the specified service collection.
/// </summary>
/// <param name="services">The service collection to add the UFile service to.</param>
/// <param name="base_path">The base path for storing files. Default is "App_Data".</param>
/// <param name="temp_foldername">The temporary folder name for storing files. Default is "tmp".</param>
/// <returns>The updated service collection.</returns>
public static class UFileExtensions
{
    public static IServiceCollection AddUFile([NotNull] this IServiceCollection services, [NotNull] string base_path = "App_Data", string temp_foldername = "tmp")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(base_path);
        ArgumentNullException.ThrowIfNull(temp_foldername);

        UFileStaticHelper.BasePath = base_path;
        UFileStaticHelper.TempFolderName = temp_foldername;

        return services;
    }
}