## The UFile library facilitates the uploading and processing of large files by handling them in smaller chunks. It provides functionality to save chunks, retry failed uploads, merge chunks into the final file, and store the completed file in a specified directoryThis approach is particularly useful for improving upload reliability and efficiency, especially for large files or unstable network conditions.

## Examples

```
npm install partial-uploader
```

```js

const file = event.target.files[0];

uploadFile = (file: File) => uploadWithPartialFile("/File/upload", file, 
    //you can add header here
    { "Authorization": `Bearer ${token}` });

let uploadResponse = await uploadFile(file);
```

```
dotnet add package UFile.PartialUploader
```

```c#

    [Route("api/[controller]")]
    [ApiController]
    public class FileController : ControllerBase
    {
        [HttpPost("upload")]
        public async Task<IActionResult> Upload()
        {
            var partial_uploader = new UFileHelper();
            return StatusCode((int)await partial_uploader.UploadChunkFiles(Request));
        }
    }

```