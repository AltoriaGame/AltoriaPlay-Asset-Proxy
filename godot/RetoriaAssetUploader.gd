extends Node
class_name RetoriaAssetUploader

## Small-file development example.
## This reads the whole file into memory. For multi-GB assets, use
## a streaming or resumable upload implementation instead.

signal upload_completed(remote_path: String, status_code: int)
signal upload_failed(remote_path: String, reason: String)

@export var api_base_url := "http://127.0.0.1:3000"
@export var bucket := "retoria-platform-assets-prod"
@export var api_key := "REPLACE_WITH_YOUR_DEV_KEY"

var _http: HTTPRequest
var _pending_path := ""


func _ready() -> void:
    _http = HTTPRequest.new()
    _http.timeout = 300.0
    _http.body_size_limit = 1024 * 1024
    add_child(_http)
    _http.request_completed.connect(_on_upload_completed)


func upload_file(local_path: String, remote_path: String) -> void:
    if not FileAccess.file_exists(local_path):
        upload_failed.emit(remote_path, "Local file does not exist.")
        return

    var file := FileAccess.open(local_path, FileAccess.READ)
    if file == null:
        upload_failed.emit(remote_path, "Could not open local file.")
        return

    var data := file.get_buffer(file.get_length())
    file.close()

    var encoded_path := _encode_relative_path(remote_path)
    if encoded_path.is_empty():
        upload_failed.emit(remote_path, "Invalid destination path.")
        return

    var url := "%s/api/buckets/%s/%s" % [
        api_base_url.trim_suffix("/"),
        bucket.uri_encode(),
        encoded_path
    ]

    _pending_path = remote_path
    var headers := PackedStringArray([
        "X-API-Key: " + api_key,
        "Content-Type: application/octet-stream"
    ])

    var err := _http.request_raw(
        url,
        headers,
        HTTPClient.METHOD_PUT,
        data
    )

    if err != OK:
        upload_failed.emit(remote_path, "Could not start upload: %s" % err)


func _encode_relative_path(value: String) -> String:
    var encoded: Array[String] = []
    for part in value.trim_prefix("/").split("/"):
        if part.is_empty() or part == "." or part == "..":
            return ""
        encoded.append(part.uri_encode())
    return "/".join(encoded)


func _on_upload_completed(
    _result: int,
    response_code: int,
    _headers: PackedStringArray,
    body: PackedByteArray
) -> void:
    if response_code >= 200 and response_code < 300:
        upload_completed.emit(_pending_path, response_code)
        print("[Retoria] Uploaded: ", _pending_path)
    else:
        var reason := "HTTP %s: %s" % [
            response_code,
            body.get_string_from_utf8()
        ]
        upload_failed.emit(_pending_path, reason)
        push_error("[Retoria] " + reason)
