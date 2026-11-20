using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>
/// Downloads a GLB file through the Retoria Asset Proxy, imports it at runtime
/// with GltfDocument, and adds the generated scene beneath this Node3D.
///
/// This is a development example. Do not ship a permanent server API key in a
/// distributed Player.exe or Studio.exe. Obtain scoped, short-lived credentials
/// from your platform backend before using this pattern in production.
/// </summary>
[GlobalClass]
public partial class RetoriaAssetLoader : Node3D
{
    [Signal]
    public delegate void AssetLoadedEventHandler(string remotePath, Node3D instance);

    [Signal]
    public delegate void AssetFailedEventHandler(string remotePath, string reason);

    [ExportGroup("Proxy")]
    [Export] public string ApiBaseUrl { get; set; } = "http://127.0.0.1:3000";
    [Export] public string Bucket { get; set; } = "retoria-platform-assets-prod";
    [Export(PropertyHint.Password)] public string ApiKey { get; set; } = "REPLACE_WITH_YOUR_DEV_KEY";

    [ExportGroup("Spawn Transform")]
    [Export] public Vector3 SpawnPosition { get; set; } = Vector3.Zero;
    [Export] public Vector3 SpawnRotationDegrees { get; set; } = Vector3.Zero;
    [Export] public Vector3 SpawnScale { get; set; } = Vector3.One;

    [ExportGroup("Import")]
    [Export(PropertyHint.Range, "1,120,1")]
    public double RequestTimeoutSeconds { get; set; } = 120.0;

    private HttpRequest? _httpRequest;
    private string _pendingRemotePath = string.Empty;
    private Node3D? _currentInstance;
    private bool _loadInProgress;

    public override void _Ready()
    {
        _httpRequest = new HttpRequest
        {
            Timeout = RequestTimeoutSeconds,
            BodySizeLimit = -1,
            UseThreads = true
        };

        AddChild(_httpRequest);
        _httpRequest.RequestCompleted += OnRequestCompleted;
    }

    /// <summary>
    /// Downloads and instantiates a GLB stored at a path relative to Bucket.
    /// Example: LoadGlb("Models/Characters/Avatar.glb").
    /// Returns immediately with the status of starting the HTTP request.
    /// Observe AssetLoaded and AssetFailed for the asynchronous result.
    /// </summary>
    public Error LoadGlb(string remotePath)
    {
        if (_httpRequest is null || !IsInsideTree())
        {
            EmitFailure(remotePath, "The loader is not ready or is outside the scene tree.");
            return Error.Unconfigured;
        }

        if (_loadInProgress)
        {
            EmitFailure(remotePath, "A download is already in progress on this loader instance.");
            return Error.Busy;
        }

        if (string.IsNullOrWhiteSpace(ApiBaseUrl) || string.IsNullOrWhiteSpace(Bucket))
        {
            EmitFailure(remotePath, "ApiBaseUrl and Bucket must be configured.");
            return Error.InvalidParameter;
        }

        if (string.IsNullOrWhiteSpace(ApiKey) || ApiKey.StartsWith("REPLACE_", StringComparison.Ordinal))
        {
            EmitFailure(remotePath, "Configure a development API key before downloading.");
            return Error.InvalidParameter;
        }

        if (!string.Equals(Path.GetExtension(remotePath), ".glb", StringComparison.OrdinalIgnoreCase))
        {
            EmitFailure(remotePath, "This loader imports .glb files only. Convert other formats to GLB first.");
            return Error.InvalidParameter;
        }

        if (!TryEncodeRemotePath(remotePath, out string encodedRemotePath))
        {
            EmitFailure(remotePath, "The asset path is invalid.");
            return Error.InvalidParameter;
        }

        string encodedBucket = Uri.EscapeDataString(Bucket);
        string url = $"{ApiBaseUrl.TrimEnd('/')}/api/buckets/{encodedBucket}/{encodedRemotePath}";
        string[] headers = { $"X-API-Key: {ApiKey}" };

        _pendingRemotePath = remotePath;
        _loadInProgress = true;

        Error requestError = _httpRequest.Request(url, headers, HttpClient.Method.Get);
        if (requestError != Error.Ok)
        {
            _loadInProgress = false;
            EmitFailure(remotePath, $"Could not start the HTTP request: {requestError}.");
            return requestError;
        }

        GD.Print($"[Retoria] Download started: {remotePath}");
        return Error.Ok;
    }

    /// <summary>Cancel the active download, if any.</summary>
    public void CancelLoad()
    {
        if (_httpRequest is null || !_loadInProgress)
            return;

        _httpRequest.CancelRequest();
        _loadInProgress = false;
        EmitFailure(_pendingRemotePath, "Download cancelled.");
    }

    /// <summary>Remove the currently instantiated asset from the scene tree.</summary>
    public void UnloadAsset()
    {
        if (_currentInstance is not null && GodotObject.IsInstanceValid(_currentInstance))
            _currentInstance.QueueFree();

        _currentInstance = null;
    }

    private void OnRequestCompleted(long result, long responseCode, string[] headers, byte[] body)
    {
        _loadInProgress = false;

        if (result != (long)HttpRequest.Result.Success)
        {
            EmitFailure(_pendingRemotePath, $"Network request failed. HTTPRequest result: {result}.");
            return;
        }

        if (responseCode != 200)
        {
            string responseText = Encoding.UTF8.GetString(body);
            EmitFailure(_pendingRemotePath, $"HTTP {responseCode}: {responseText}");
            return;
        }

        if (body.Length == 0)
        {
            EmitFailure(_pendingRemotePath, "The downloaded GLB file is empty.");
            return;
        }

        try
        {
            var gltfDocument = new GltfDocument();
            var gltfState = new GltfState
            {
                // GLB files with embedded buffers and textures can use an empty base path.
                // External .gltf dependencies require a separately managed base path.
                BasePath = string.Empty
            };

            Error importError = gltfDocument.AppendFromBuffer(body, string.Empty, gltfState);
            if (importError != Error.Ok)
            {
                EmitFailure(_pendingRemotePath, $"GLB import failed: {importError}.");
                return;
            }

            Node? generatedScene = gltfDocument.GenerateScene(gltfState);
            if (generatedScene is null)
            {
                EmitFailure(_pendingRemotePath, "Godot could not generate a scene from this GLB.");
                return;
            }

            UnloadAsset();

            var instanceContainer = new Node3D
            {
                Name = "RetoriaRuntimeAsset",
                Position = SpawnPosition,
                RotationDegrees = SpawnRotationDegrees,
                Scale = SpawnScale
            };

            AddChild(instanceContainer);
            instanceContainer.AddChild(generatedScene);
            _currentInstance = instanceContainer;

            GD.Print($"[Retoria] Asset instantiated: {_pendingRemotePath} ({body.Length} bytes)");
            EmitSignal(SignalName.AssetLoaded, _pendingRemotePath, instanceContainer);
        }
        catch (Exception exception)
        {
            EmitFailure(_pendingRemotePath, $"GLB processing failed: {exception.Message}");
        }
    }

    private void EmitFailure(string remotePath, string reason)
    {
        GD.PushError($"[Retoria] {reason}");
        EmitSignal(SignalName.AssetFailed, remotePath, reason);
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
}
