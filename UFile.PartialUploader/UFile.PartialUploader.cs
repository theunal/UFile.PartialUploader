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
public interface IUFileService
{
    Task<HttpStatusCode> UploadChunkFiles([NotNull] HttpRequest Request);
    Task<UploadWithPartialFileReturnModel> UploadWithPartialFileAsync([NotNull] string url, [NotNull] string filePath, Dictionary<string, string>? headers = null, int chunkSize = 26214400);
}
public class UFileService : IUFileService
{
    /// <summary>
    /// Handles the upload of chunked files and assembles them into a complete file when all chunks are uploaded.
    /// </summary>
    /// <param name="Request">The HTTP request containing the file chunks and related metadata.</param>
    /// <returns>The HTTP status code indicating the result of the upload operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when the Request parameter is null.</exception>
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
        var dirPath = Path.Combine(UFileHelper.BasePath, chunk_files_folder_name, fileGuid);
        var save = await UFileHelper.SaveWithRetryAsync(dirPath, file.FileName, file, 3, 3);
        if (save is false) return HttpStatusCode.BadRequest;

        if (isDone)
        {
            byte[] bytes = new byte[totalSize];
            using var ms = new MemoryStream();

            var chunkRes = await UFileHelper.ChunkMerger(ms, totalChunks, dirPath, filename);
            if (chunkRes is false)
            {
                Directory.Delete(dirPath, true);
                return HttpStatusCode.NotAcceptable;
            }

            bytes = ms.ToArray();
            await ms.FlushAsync();
            ms.Close();

            var temp_file_path = Path.Combine(dirPath.Replace(chunk_files_folder_name, UFileHelper.TempFolderName));
            await UFileHelper.SaveFile(temp_file_path, filename, bytes);

            Directory.Delete(dirPath, true);
        }

        return HttpStatusCode.OK;
    }

    /// <summary>
    /// Uploads a file to the specified URL, either in one piece or in chunks if the file is too large.
    /// </summary>
    /// <param name="url">The URL to which the file will be uploaded.</param>
    /// <param name="fileStream">The stream of the file to be uploaded.</param>
    /// <param name="fileName">The name of the file to be uploaded.</param>
    /// <param name="headers">Optional headers to include in the upload request.</param>
    /// <param name="chunkSize">The size of each chunk in bytes. Default is 25MB.</param>
    /// <returns>A tuple containing success status, temp folder ID, and a message.</returns>
    /// <exception cref="ArgumentNullException">Thrown when URL, fileName, or fileStream is null.</exception>
    /// <exception cref="InvalidDataException">Thrown when a chunk is null or file could not be loaded.</exception>
    public async Task<UploadWithPartialFileReturnModel> UploadWithPartialFileAsync([NotNull] string url, [NotNull] string filePath, Dictionary<string, string>? headers = null, int chunkSize = 26214400)
    {
        if (string.IsNullOrEmpty(url))
            return new UploadWithPartialFileReturnModel
            {
                id = null,
                success = false,
                message = "Url is null."
            };

        if (string.IsNullOrEmpty(filePath))
            return new UploadWithPartialFileReturnModel
            {
                id = null,
                success = false,
                message = "File path is null."
            };

        if (File.Exists(filePath) is false)
            return new UploadWithPartialFileReturnModel
            {
                id = null,
                success = false,
                message = $"File not found in this path. ({filePath})"
            };

        await Task.Delay(50);
        var id = Guid.NewGuid().ToString();

        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        var fileName = Path.GetFileName(filePath);
        if (fileStream.Length <= chunkSize) // small file upload
        {
            var res = await UploadAsync(url, fileStream, fileName, 1, (int)fileStream.Length, id, 0, headers);
            if (!res)
                return new UploadWithPartialFileReturnModel
                {
                    id = null,
                    success = false,
                    message = "file could not be loaded"
                };
        }
        else
        {
            // big file upload - partial
            var chunks = SplitFileIntoChunks(fileStream, chunkSize);

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                var res = await UploadAsync(url, chunk, fileName, chunks.Count, (int)fileStream.Length, id, i, headers);
                if (!res)
                    throw new InvalidDataException("File could not be loaded.");

                await Task.Delay(550);
            }
        }

        return new UploadWithPartialFileReturnModel
        {
            id = id,
            success = true,
            message = "file uploaded successfully"
        };
    }


    /// <summary>
    /// Uploads a file chunk to the specified URL.
    /// </summary>
    /// <param name="url">The URL to which the chunk will be uploaded.</param>
    /// <param name="chunk">The file chunk to be uploaded.</param>
    /// <param name="fileName">The name of the file being uploaded.</param>
    /// <param name="chunksLength">The total number of chunks.</param>
    /// <param name="fileSize">The size of the file in bytes.</param>
    /// <param name="fileGuid">The unique identifier for the file upload session.</param>
    /// <param name="index">The index of the current chunk.</param>
    /// <param name="headers">Optional headers to include in the upload request.</param>
    /// <returns>A task that represents the asynchronous operation, containing the success status.</returns>
    public static Task<bool> UploadAsync(string url, Stream chunk, string fileName, int chunksLength, int fileSize, string fileGuid, int index, Dictionary<string, string>? headers)
    {
        var formData = new MultipartFormDataContent();
        var streamContent = new StreamContent(chunk);
        formData.Add(streamContent, "file", $"{fileName}_chunk_{index}");
        formData.Add(new StringContent(fileGuid), "fileGuid");
        var isDone = chunksLength == index + 1;
        formData.Add(new StringContent(isDone.ToString()), "isDone");
        formData.Add(new StringContent(fileSize.ToString()), "totalSize");
        formData.Add(new StringContent(chunksLength.ToString()), "totalChunks");
        formData.Add(new StringContent(fileName), "filename");

        return GetResponseAsync(url, formData, headers);
    }

    /// <summary>
    /// Gets the response from the server after attempting to upload the form data.
    /// </summary>
    /// <param name="url">The URL to which the form data will be sent.</param>
    /// <param name="formData">The form data to be uploaded.</param>
    /// <param name="headers">Optional headers to include in the request.</param>
    /// <returns>A task that represents the asynchronous operation, containing the success status.</returns>
    private static async Task<bool> GetResponseAsync(string url, MultipartFormDataContent formData, Dictionary<string, string>? headers)
    {
        try
        {
            var res = await UploadSubscribeAsync(url, formData, headers);
            if (res.StatusCode == System.Net.HttpStatusCode.NotAcceptable || res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return false;
        }
        catch (Exception)
        {
            await Task.Delay(500);
            var res = await UploadSubscribeAsync(url, formData, headers);
            if (!res.IsSuccessStatusCode)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Sends the form data to the specified URL using an HTTP POST request.
    /// </summary>
    /// <param name="url">The URL to which the form data will be sent.</param>
    /// <param name="formData">The form data to be uploaded.</param>
    /// <param name="headers">Optional headers to include in the request.</param>
    /// <returns>A task that represents the asynchronous operation, containing the HTTP response.</returns>
    private static async Task<HttpResponseMessage> UploadSubscribeAsync(string url, MultipartFormDataContent formData, Dictionary<string, string>? headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        headers?.ToList()?.ForEach(header => request.Headers.Add(header.Key, header.Value));
        request.Content = formData;

        using var client = new HttpClient();
        return await client.SendAsync(request);
    }

    /// <summary>
    /// Splits the file stream into chunks of the specified size.
    /// </summary>
    /// <param name="fileStream">The file stream to be split into chunks.</param>
    /// <param name="chunkSize">The size of each chunk in bytes.</param>
    /// <returns>A list of streams representing the file chunks.</returns>
    public static List<Stream> SplitFileIntoChunks(Stream fileStream, int chunkSize)
    {
        var chunks = new List<Stream>();
        var buffer = new byte[chunkSize];
        int bytesRead;

        while ((bytesRead = fileStream.Read(buffer, 0, chunkSize)) > 0)
        {
            var chunk = new MemoryStream(buffer.Take(bytesRead).ToArray());
            chunks.Add(chunk);
        }

        return chunks;
    }
}

public class UploadWithPartialFileReturnModel
{
    public string? id { get; set; }
    public string? message { get; set; }
    public bool success { get; set; }
}

public static class UFileHelper
{
    public static string BasePath = null!;
    public static string TempFolderName = null!;
    public static int UploadChunkSize = 26214400;

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
        for (var i = 0; i < totalChunks; i++)
        {
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
    /// Retrieves the FileInfo object for the first file in a specified temporary folder.
    /// </summary>
    /// <param name="folder_id">The identifier of the folder to search for the file.</param>
    /// <returns>The FileInfo object of the first file found in the specified folder, or null if the folder does not exist or contains no files.</returns>
    public static FileInfo? GetFileInfo(string folder_id)
    {
        var folderPath = Path.Combine(Directory.GetCurrentDirectory(), BasePath, TempFolderName, folder_id);
        if (Directory.Exists(folderPath) is false) return null;

        var file_path = Directory.GetFiles(folderPath)?.FirstOrDefault();
        if (file_path is null) return null;

        return new FileInfo(file_path);
    }

    /// <summary>
    /// Removes a temporary folder identified by the given folder ID if it exists.
    /// </summary>
    /// <param name="folder_id">The ID of the folder to be removed.</param>
    public static void RemoveTempFolder(string folder_id)
    {
        var folderPath = Path.Combine(Directory.GetCurrentDirectory(), BasePath, TempFolderName, folder_id);
        if (Directory.Exists(folderPath)) Directory.Delete(folderPath, true);
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
    public static IServiceCollection AddUFile([NotNull] this IServiceCollection services, [NotNull] string base_path = "App_Data", string temp_foldername = "tmp", int upload_chunk_size_for_server_side = 26214400)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(base_path);
        ArgumentNullException.ThrowIfNull(temp_foldername);

        UFileHelper.BasePath = base_path;
        UFileHelper.TempFolderName = temp_foldername;
        UFileHelper.UploadChunkSize = upload_chunk_size_for_server_side;

        services.AddScoped<IUFileService, UFileService>();

        return services;
    }
}