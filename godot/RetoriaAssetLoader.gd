extends Node3D
class_name RetoriaAssetLoader

## Development example only.
## Do not ship a permanent server API key inside Player.exe.
## In production, obtain a short-lived access token from your game backend.

signal asset_loaded(remote_path: String, instance: Node3D)
signal asset_failed(remote_path: String, reason: String)

@export_group("Proxy")
@export var api_base_url := "http://127.0.0.1:3000"
@export var bucket := "retoria-platform-assets-prod"
@export var api_key := "REPLACE_WITH_YOUR_DEV_KEY"

@export_group("Spawn")
@export var spawn_position := Vector3.ZERO
@export var spawn_rotation_degrees := Vector3.ZERO
@export var spawn_scale := Vector3.ONE

var _http: HTTPRequest
var _pending_path := ""
var _current_instance: Node3D


func _ready() -> void:
    _http = HTTPRequest.new()
    _http.name = "AssetHTTPRequest"
    _http.timeout = 120.0
    _http.body_size_limit = -1
    _http.use_threads = true
    add_child(_http)
    _http.request_completed.connect(_on_request_completed)


func load_glb(remote_path: String) -> void:
    if _http.get_http_client_status() != HTTPClient.STATUS_DISCONNECTED:
        push_warning("An asset request may already be in progress.")

    var encoded_path := _encode_relative_path(remote_path)
    if encoded_path.is_empty():
        asset_failed.emit(remote_path, "Invalid asset path.")
        return

    var encoded_bucket := bucket.uri_encode()
    var url := "%s/api/buckets/%s/%s" % [
        api_base_url.trim_suffix("/"),
        encoded_bucket,
        encoded_path
    ]

    _pending_path = remote_path
    var headers := PackedStringArray([
        "X-API-Key: " + api_key
    ])

    var err := _http.request(url, headers, HTTPClient.METHOD_GET)
    if err != OK:
        asset_failed.emit(remote_path, "Could not start HTTP request: %s" % err)


func _encode_relative_path(value: String) -> String:
    var parts := value.trim_prefix("/").split("/")
    var encoded: Array[String] = []

    for part in parts:
        if part.is_empty() or part == "." or part == "..":
            return ""
        encoded.append(part.uri_encode())

    return "/".join(encoded)


func _on_request_completed(
    result: int,
    response_code: int,
    _headers: PackedStringArray,
    body: PackedByteArray
) -> void:
    if result != HTTPRequest.RESULT_SUCCESS:
        _fail("Network error: %s" % result)
        return

    if response_code != 200:
        _fail("HTTP %s: %s" % [response_code, body.get_string_from_utf8()])
        return

    if body.is_empty():
        _fail("The downloaded asset is empty.")
        return

    var document := GLTFDocument.new()
    var state := GLTFState.new()

    var err := document.append_from_buffer(body, "", state)
    if err != OK:
        _fail("GLB import failed: %s" % err)
        return

    var generated_scene := document.generate_scene(state)
    if generated_scene == null:
        _fail("Godot could not generate a scene from this GLB.")
        return

    if is_instance_valid(_current_instance):
        _current_instance.queue_free()

    var container := Node3D.new()
    container.name = "RuntimeAsset"
    container.position = spawn_position
    container.rotation_degrees = spawn_rotation_degrees
    container.scale = spawn_scale
    add_child(container)
    container.add_child(generated_scene)

    _current_instance = container
    asset_loaded.emit(_pending_path, container)
    print("[Retoria] Loaded: ", _pending_path)


func _fail(reason: String) -> void:
    push_error("[Retoria] " + reason)
    asset_failed.emit(_pending_path, reason)


func unload_asset() -> void:
    if is_instance_valid(_current_instance):
        _current_instance.queue_free()
    _current_instance = null
