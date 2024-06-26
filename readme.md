## The UFile library facilitates the uploading and processing of large files by handling them in smaller chunks. It provides functionality to save chunks, retry failed uploads, merge chunks into the final file, and store the completed file in a specified directoryThis approach is particularly useful for improving upload reliability and efficiency, especially for large files or unstable network conditions.

## Examples

```
dotnet add package UFile.PartialUploader
```

```c#
public static IServiceCollection AddServices(this IServiceCollection services, IConfiguration conf)
{
    // Adds the UFile service to the specified service collection.
    services.AddUFile("App_Data", "tmp");

    return services;
}
```

```c#

    [Route("api/[controller]")]
    [ApiController]
    public class FileController(IUFileService uFileService) : ControllerBase
    {
        [HttpPost("upload")]
        public async Task<IActionResult> Upload() => StatusCode((int)await uFileService.UploadChunkFiles(Request));
    }

```

```
npm install partial-uploader
```

```js

const file = event.target.files[0];

uploadFile = (file: File) => uploadWithPartialFile("/File/upload", file, 
    //you can add header here
    { "Authorization": `Bearer ${token}` });

let uploadResponse = await uploadFile(file);
if (uploadResponse.success)
{
    // your file will be inside the folder at this location
    const path = 'App_Data/tmp/' + uploadResponse.id;
}

```

```c#
    var fileInfo = UFileHelper.GetFileInfo(tempFolderId);
```

```c#

// Server Side Upload

    public class Test(IUFileService uFileService)
    {
        public async Task Upload()
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), "App_Data", "path/to/your/file.txt");

            var res = await UploadWithPartialFileAsync("https://example.com/upload", path, new Dictionary<string, string>
            {
                { "Authorization", "Bearer your_token" }
            });
        }
    }

```