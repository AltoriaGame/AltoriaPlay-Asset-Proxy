RETORIA ASSET PROXY
===================

A Node.js HTTP proxy for accessing files stored in a SeaweedFS Filer.
The repository includes Godot 4 examples in GDScript and C#/.NET for
uploading assets and loading GLB files at runtime.

CONTENTS
--------
server.js
    Node.js HTTP API proxy for SeaweedFS.

package.json
    Project metadata and start scripts.

.env.example
    Example configuration. Copy it to .env and edit it locally.

.gitignore
    Excludes local secrets and generated files from Git.

godot/RetoriaAssetLoader.gd
    GDScript example: downloads a GLB through the proxy and instantiates it.

godot/RetoriaAssetLoader.cs
    Godot .NET C# example: downloads a GLB through the proxy, imports it with
    GltfDocument, and instantiates the generated scene.

godot/RetoriaAssetUploader.gd
    GDScript example: uploads a local file through the proxy.

godot/RetoriaAssetUploader.cs
    Godot .NET C# example: uploads a local file through the proxy using HTTP PUT.

REQUIREMENTS
------------
- Node.js 20.6.0 or newer.
- A running SeaweedFS Filer reachable by the Node.js process.
- Godot 4.x for the GDScript example.
- A Godot .NET build and a C#-enabled Godot project for the C# example.

INSTALLATION
------------
1. Clone or download this repository.
2. Open a terminal in the repository directory.
3. Copy .env.example to .env.
4. Generate a strong API key:

   node -e "console.log(require('crypto').randomBytes(48).toString('hex'))"

5. Put the generated value in .env as API_KEY.
6. Set SEAWEEDFS_URL to the URL of your SeaweedFS Filer.
7. Start the service:

   npm start

The Node.js service uses built-in modules and does not require npm packages.

CONFIGURATION
-------------
HOST
    Address on which the proxy listens. Use 127.0.0.1 for local-only access.
    Use 0.0.0.0 only when remote devices need to connect, and protect the
    service with a firewall and HTTPS reverse proxy.

PORT
    HTTP port for the proxy. Default: 3000.

SEAWEEDFS_URL
    Base URL of the SeaweedFS Filer, for example:
    http://127.0.0.1:8888

API_KEY
    Shared-secret development/server key. At least 32 characters are required.

MAX_UPLOAD_SIZE
    Maximum accepted upload size in bytes. Default: 2147483648 (2 GiB).

DEFAULT_BUCKET
    Informational default bucket name. API routes accept an explicit bucket.

CORS_ORIGINS
    Comma-separated browser origins. CORS does not apply to native Godot
    HTTPRequest clients.

HTTP ENDPOINTS
--------------
GET    /health
       Public health check. Does not expose storage credentials.

GET    /api/buckets
       List buckets through the configured Filer.

GET    /api/buckets/{bucket}/
       List a bucket directory.

GET    /api/buckets/{bucket}/{path}
       Download a file.

HEAD   /api/buckets/{bucket}/{path}
       Request file metadata/headers.

PUT    /api/buckets/{bucket}/{path}
       Upload or replace a file.

POST   /api/buckets/{bucket}/{directory}/
       Forward a directory creation request to the Filer.

DELETE /api/buckets/{bucket}/{path}
       Delete a file or directory. Use recursive=true when supported by the
       configured Filer endpoint.

All storage endpoints require either:

    X-API-Key: YOUR_API_KEY

or:

    Authorization: Bearer YOUR_TOKEN

GODOT 4: C#/.NET SETUP
----------------------
The C# samples are intended for a Godot .NET project. They use Godot's HttpRequest,
FileAccess, GltfDocument, and C# signal bindings; no additional NuGet package is
required. See the C# runtime GLB loader section for RetoriaAssetLoader.cs.

1. Create or open a Godot project using the Godot .NET editor build.
2. Ensure the project has a C# project file (.csproj) and builds successfully.
3. Copy godot/RetoriaAssetUploader.cs and/or godot/RetoriaAssetLoader.cs into the project, for example into:

       res://Scripts/Networking/RetoriaAssetUploader.cs

4. Build the project from the Godot editor (Build button) or with dotnet build.
5. Add a Node to a scene and attach RetoriaAssetUploader.cs to it.
6. Configure ApiBaseUrl, Bucket, and ApiKey in the Inspector, or set them from C#.
7. For uploads, call UploadFile(). For runtime model downloads, call LoadGlb().

Example C# usage from another Node:

    using Godot;

    public partial class UploadExample : Node
    {
        public override void _Ready()
        {
            var uploader = GetNode<RetoriaAssetUploader>("RetoriaAssetUploader");
            uploader.UploadCompleted += OnUploadCompleted;
            uploader.UploadFailed += OnUploadFailed;

            uploader.UploadFile(
                "C:/Retoria/Assets/Avatar.glb",
                "Models/Characters/Avatar.glb"
            );
        }

        private void OnUploadCompleted(string remotePath, long responseCode)
        {
            GD.Print($"Uploaded {remotePath}. HTTP {responseCode}");
        }

        private void OnUploadFailed(string remotePath, string reason)
        {
            GD.PushError($"Could not upload {remotePath}: {reason}");
        }
    }

The first argument accepts a Godot path (res:// or user://) or an absolute
filesystem path. For exported applications, user:// or an absolute path chosen
by the user is usually more appropriate than relying on source files in res://.
The destination path is relative to the selected bucket. Do not include the
bucket name again in the destination path.

Example destination:

    Bucket:        retoria-platform-assets-prod
    Remote path:   Models/Characters/Avatar.glb
    Proxy URL:     http://127.0.0.1:3000/api/buckets/retoria-platform-assets-prod/Models/Characters/Avatar.glb

The C# uploader sends the file bytes as an HTTP PUT request with an appropriate
Content-Type for common formats such as .glb, .gltf, .obj, and .stl. Other file
formats are sent as application/octet-stream.

Signals:
    UploadCompleted(remotePath, responseCode)
    UploadFailed(remotePath, reason)

The C# uploader buffers the complete file in memory before sending it. It is
suitable as a small-file integration example, but it is not a streaming uploader.
The current implementation rejects files larger than the maximum byte-array
size supported by the .NET runtime. For large 3D projects, use a streaming or
resumable upload protocol and avoid loading multi-gigabyte files into RAM.

GODOT 4: C#/.NET RUNTIME GLB LOADER
-----------------------------------
The C# loader downloads a .glb file through the proxy and instantiates it in the
running scene using Godot's GltfDocument and GltfState classes. No extra NuGet
package is required.

1. Open the project with the Godot .NET editor build.
2. Copy godot/RetoriaAssetLoader.cs into your project, for example:

       res://Scripts/Networking/RetoriaAssetLoader.cs

3. Build the C# project.
4. Add a Node3D to the scene and attach RetoriaAssetLoader.cs to it.
5. Configure ApiBaseUrl, Bucket, ApiKey, and the spawn transform in the Inspector.
6. Call LoadGlb() with a path relative to the bucket.

Example C# usage:

    using Godot;

    public partial class RuntimeAssetExample : Node
    {
        public override void _Ready()
        {
            var loader = GetNode<RetoriaAssetLoader>("RetoriaAssetLoader");
            loader.AssetLoaded += OnAssetLoaded;
            loader.AssetFailed += OnAssetFailed;

            loader.LoadGlb("Models/Characters/Avatar.glb");
        }

        private void OnAssetLoaded(string remotePath, Node3D instance)
        {
            GD.Print($"Loaded {remotePath}: {instance.Name}");
        }

        private void OnAssetFailed(string remotePath, string reason)
        {
            GD.PushError($"Could not load {remotePath}: {reason}");
        }
    }

The loader exposes these methods:

    LoadGlb(remotePath)
        Starts an asynchronous download and runtime import. Returns an Error
        value describing whether the HTTP request could be started.

    CancelLoad()
        Cancels the current download.

    UnloadAsset()
        Removes the currently instantiated model from the scene tree.

Signals:

    AssetLoaded(remotePath, instance)
    AssetFailed(remotePath, reason)

The example imports .glb files with embedded buffers and textures. It does not
automatically retrieve external dependencies from a .gltf file. It also does not
convert .blend, .fbx, .obj, .stl, or .ATPF files. Convert those formats to GLB
(or implement a dedicated importer) before calling LoadGlb().

The complete response body is held in memory and GLB parsing/generation occurs
after download. This is a straightforward runtime example, not a streaming
loader for extremely large assets. Configure appropriate size limits and add
asset caching for production use.

GODOT 4: GDSCRIPT EXAMPLES
--------------------------
RetoriaAssetUploader.gd:
    Attach it to a Node, configure the proxy URL, bucket, and development key,
    then call:

        $RetoriaAssetUploader.upload_file(
            "res://Assets/Avatar.glb",
            "Models/Characters/Avatar.glb"
        )

RetoriaAssetLoader.gd:
    Attach it to a Node3D and call:

        $RetoriaAssetLoader.load_asset("Models/Characters/Avatar.glb")

The loader downloads GLB bytes using HTTPRequest, parses them with
GLTFDocument.append_from_buffer(), generates a Godot scene, and adds it beneath
a Node3D container. The sample expects a GLB with embedded resources. A .gltf
file referencing external .bin or texture files needs additional dependency
retrieval and path handling.

FORMAT SUPPORT AND LIMITATIONS
------------------------------
The proxy stores and transfers file bytes. It does not convert files into GLB.
The runtime loader provided here imports GLB, not every format accepted by the
storage API. Formats such as .blend, .fbx, .obj, .stl, and .ATPF require a suitable
runtime importer, conversion pipeline, or application-specific parser before
they can be instantiated in a Godot scene.

SECURITY NOTES
--------------
- Never commit .env. Commit only .env.example.
- Never embed a permanent server API key in a distributed Player.exe or
  Studio.exe. A key embedded in a client can be extracted.
- For production, authenticate users through your platform backend and issue
  short-lived, scoped tokens or signed download URLs. The sample C# class uses
  X-API-Key only to demonstrate local development.
- Use HTTPS outside localhost.
- Keep SeaweedFS private; expose only the proxy where possible.
- Add authorization checks for bucket, asset owner, and operation before using
  this as a public multi-user service.
- Add rate limits, audit logs, malware/content scanning, and stricter upload
  validation before production deployment.
- Verify the exact SeaweedFS Filer API semantics and bucket layout in your
  deployment before exposing write/delete operations publicly.

LIMITATIONS
-----------
This is an example proxy, not a complete asset-management platform. It does not
provide asset ownership, moderation, version history, metadata database,
thumbnail generation, model conversion, or per-user authorization. The API key
is a shared-secret development mechanism.

LICENSE
-------
No license is included. Add a LICENSE file before publishing if you want to
grant others explicit reuse rights.
