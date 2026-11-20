using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Uploads local files to the Retoria Asset Proxy API using HTTP PUT.
///
/// This example reads the entire file into memory before uploading it.
/// Use a streaming or resumable-upload protocol for very large assets.
/// Do not ship a permanent server API key inside a distributed game client.
/// </summary>
[GlobalClass]
public partial class RetoriaAssetUploader : Node
{
    [Signal]
    public delegate void UploadCompletedEventHandler(string remotePath, long responseCode);

    [Signal]
    public delegate void UploadFailedEventHandler(string remotePath, string reason);

    [ExportGroup("Proxy")]
    [Export] public string ApiBaseUrl { get; set; } = "http://127.0.0.1:3000";
    [Export] public string Bucket { get; set; } = "retoria-platform-assets-prod";
    [Export(PropertyHint.Password)] public string ApiKey { get; set; } = "REPLACE_WITH_YOUR_DEV_KEY";

    private HttpRequest _httpRequest = null!;
    private string _pendingRemotePath = string.Empty;
    private bool _uploadInProgress;

    public override void _Ready()
    {
        _httpRequest = new HttpRequest
        {
            Timeout = 300.0,
            // This limits the response body, not the upload body. SeaweedFS should
            // normally return a small response for a successful file upload.
            BodySizeLimit = 1024 * 1024,
            UseThreads = true
        };

        AddChild(_httpRequest);
        _httpRequest.RequestCompleted += OnRequestCompleted;
    }

    /// <summary>
    /// Uploads a local file to the configured bucket.
    /// localPath accepts Godot paths (res://, user://) or absolute filesystem paths.
    /// remotePath is relative to the bucket, for example Models/Avatar.glb.
    /// The HTTP upload completes asynchronously; observe UploadCompleted/UploadFailed.
    /// </summary>
    public Error UploadFile(string localPath, string remotePath)
    {
        if (_httpRequest is null || !IsInsideTree())
        {
            EmitFailure(remotePath, "The uploader node is not ready or is outside the scene tree.");
            return Error.Unconfigured;
        }

        if (_uploadInProgress)
        {
            EmitFailure(remotePath, "An upload is already in progress on this uploader instance.");
            return Error.Busy;
        }

        if (string.IsNullOrWhiteSpace(ApiBaseUrl) || string.IsNullOrWhiteSpace(Bucket))
        {
            EmitFailure(remotePath, "ApiBaseUrl and Bucket must be configured.");
            return Error.InvalidParameter;
        }

        if (string.IsNullOrWhiteSpace(ApiKey) || ApiKey.StartsWith("REPLACE_", StringComparison.Ordinal))
        {
            EmitFailure(remotePath, "Configure a development API key before uploading.");
            return Error.Unauthorized;
        }

        if (!TryEncodeRemotePath(remotePath, out string encodedRemotePath))
        {
            EmitFailure(remotePath, "The destination path is invalid.");
            return Error.InvalidParameter;
        }

        byte[] fileData;
        try
        {
            using FileAccess? file = FileAccess.Open(localPath, FileAccess.ModeFlags.Read);
            if (file is null)
            {
                Error openError = FileAccess.GetOpenError();
                EmitFailure(localPath, $"Could not open the local file. Godot error: {openError}.");
                return openError;
            }

            ulong fileLength = file.GetLength();
            if (fileLength > int.MaxValue)
            {
                EmitFailure(localPath, "This example supports files up to 2 GB minus 1 byte because it buffers the file in memory.");
                return Error.InvalidParameter;
            }

            fileData = file.GetBuffer((long)fileLength);
            if ((ulong)fileData.Length != fileLength)
            {
                EmitFailure(localPath, "Could not read the complete local file.");
                return Error.Failed;
            }
        }
        catch (Exception exception)
        {
            EmitFailure(localPath, $"File read failed: {exception.Message}");
            return Error.Failed;
        }

        string encodedBucket = Uri.EscapeDataString(Bucket);
        string url = $"{ApiBaseUrl.TrimEnd('/')}/api/buckets/{encodedBucket}/{encodedRemotePath}";

        string contentType = GetContentType(remotePath);
        string[] headers =
        {
            $"X-API-Key: {ApiKey}",
            $"Content-Type: {contentType}"
        };

        _pendingRemotePath = remotePath;
        _uploadInProgress = true;

        Error requestError = _httpRequest.RequestRaw(
            url,
            headers,
            HttpClient.Method.Put,
            fileData
        );

        if (requestError != Error.Ok)
        {
            _uploadInProgress = false;
            EmitFailure(remotePath, $"Could not start HTTP upload: {requestError}.");
            return requestError;
        }

        GD.Print($"[Retoria] Upload started: {remotePath} ({fileData.Length} bytes)");
        return Error.Ok;
    }

    /// <summary>Cancel the active HTTP request, if any.</summary>
    public void CancelUpload()
    {
        if (_httpRequest is null || !_uploadInProgress)
            return;

        _httpRequest.CancelRequest();
        _uploadInProgress = false;
        EmitFailure(_pendingRemotePath, "Upload cancelled.");
    }

    private void OnRequestCompleted(long result, long responseCode, string[] headers, byte[] body)
    {
        _uploadInProgress = false;

        // HTTPRequest.RESULT_SUCCESS is 0.
        if (result != 0)
        {
            EmitFailure(_pendingRemotePath, $"Network request failed. HTTPRequest result: {result}.");
            return;
        }

        if (responseCode < 200 || responseCode >= 300)
        {
            string responseText = System.Text.Encoding.UTF8.GetString(body);
            EmitFailure(_pendingRemotePath, $"HTTP {responseCode}: {responseText}");
            return;
        }

        GD.Print($"[Retoria] Upload completed: {_pendingRemotePath} (HTTP {responseCode})");
        EmitSignal(SignalName.UploadCompleted, _pendingRemotePath, responseCode);
    }

    private void EmitFailure(string remotePath, string reason)
    {
        GD.PushError($"[Retoria] {reason}");
        EmitSignal(SignalName.UploadFailed, remotePath, reason);
    }

    private static bool TryEncodeRemotePath(string remotePath, out string encodedPath)
    {
        encodedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(remotePath) || remotePath.Contains('\\') || remotePath.Contains('\0'))
            return false;

        string[] segments = remotePath.Trim('/').Split('/');
        if (segments.Length == 0)
            return false;

        var encodedSegments = new List<string>(segments.Length);
        foreach (string segment in segments)
        {
            if (string.IsNullOrEmpty(segment) || segment == "." || segment == "..")
                return false;

            encodedSegments.Add(Uri.EscapeDataString(segment));
        }

        encodedPath = string.Join("/", encodedSegments);
        return true;
    }

    private static string GetContentType(string filePath)
    {
        string extension = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".glb" => "model/gltf-binary",
            ".gltf" => "model/gltf+json",
            ".stl" => "model/stl",
            ".obj" or ".mtl" => "text/plain",
            ".json" => "application/json",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            _ => "application/octet-stream"
        };
    }
}
