using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

[assembly: InternalsVisibleTo("NomadLink.Editor.Tests")]

namespace Malloc.NomadLink
{
    internal sealed class NomadLinkSession
    {
        internal const int JsonLimit = 50 * 1024 * 1024;
        internal const int BinaryLimit = 512 * 1024 * 1024;
        internal const long QueueLimit = 512L * 1024 * 1024;

        private const int ProtocolVersion = 1;
        private const string BridgeVersion = "0.1.0";
        private const string PreviewRootName = "Nomad Link Preview";
        private const float MatrixTolerance = 0.0001f;

        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        private static readonly string[] Capabilities =
        {
            "scene_edits",
            "object_state",
            "session_config",
            "mesh_instance",
            "mesh_delta_receive",
            "mesh_attributes_receive"
        };

        private readonly ConcurrentQueue<WorkerItem> incoming =
            new ConcurrentQueue<WorkerItem>();
        private readonly Queue<byte[]> outgoing = new Queue<byte[]>();
        private readonly object outgoingLock = new object();
        private readonly Dictionary<string, ObjectEntry> objects =
            new Dictionary<string, ObjectEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, GeometryEntry> geometries =
            new Dictionary<string, GeometryEntry>(StringComparer.Ordinal);

        private NomadLinkScene owner;
        private TcpClient client;
        private Thread worker;
        private GameObject previewRoot;
        private long queuedBytes;
        private volatile bool stopRequested;
        private volatile bool closeAfterWrites;
        private bool running;
        private bool closing;
        private bool callbacksSubscribed;
        private bool helloReceived;
        private bool overrideSent;
        private bool configurationEstablished;
        private bool liveSync;
        private bool syncObjects;
        private string activeSource = "none";
        private string host;
        private int port;
        private string status = "Disabled";
        private string error = string.Empty;
        private string failureMessage = string.Empty;

        internal static NomadLinkSession Instance { get; } =
            new NomadLinkSession();

        internal event Action InspectorStateChanged;

        private NomadLinkSession()
        {
        }

        internal void Enable(NomadLinkScene scene, string targetHost, int targetPort)
        {
            if (running && owner != scene)
            {
                return;
            }

            StopInternal("Disabled", string.Empty, false);
            owner = scene;
            if (!IsValidController(scene))
            {
                SetStoppedError("The scene controller is unavailable.");
                return;
            }

            if (string.IsNullOrWhiteSpace(targetHost) ||
                targetPort < 1 || targetPort > 65535)
            {
                SetStoppedError("Enter a valid host and port.");
                return;
            }

            host = targetHost.Trim();
            port = targetPort;
            CreatePreviewRoot();
            SubscribeCallbacks();
            running = true;
            stopRequested = false;
            closeAfterWrites = false;
            status = "Connecting";
            error = string.Empty;
            NotifyInspector();

            worker = new Thread(WorkerRun)
            {
                IsBackground = true,
                Name = "Nomad Link TCP"
            };
            worker.Start();
        }

        internal void Disable(NomadLinkScene scene)
        {
            if (owner == scene)
            {
                StopInternal("Disabled", string.Empty, false);
            }
        }

        internal SessionSnapshot GetSnapshot(NomadLinkScene scene)
        {
            if (running && owner != scene)
            {
                return SessionSnapshot.OtherScene();
            }

            var ownsSession = owner == scene;
            if (ownsSession && !ValidateAllEntries(out var validationError))
            {
                StopInternal("Error", validationError, true);
            }

            var rows = ownsSession && running
                ? CreateMaterialRows()
                : Array.Empty<MaterialRowSnapshot>();
            return CreateSnapshot(ownsSession, rows);
        }

        internal bool SetMaterial(
            NomadLinkScene scene,
            string meshId,
            Material material)
        {
            if (!running || owner != scene ||
                !objects.TryGetValue(meshId, out var entry))
            {
                return false;
            }

            if (!ValidateEntry(entry, out var validationError))
            {
                StopInternal("Error", validationError, true);
                return false;
            }

            entry.Renderer.sharedMaterial = material;
            return true;
        }

        internal void Pump()
        {
            if (!running)
            {
                return;
            }

            if (!IsValidController(owner))
            {
                StopInternal("Disabled", string.Empty, false);
                return;
            }

            while (incoming.TryDequeue(out var item))
            {
                ProcessWorkerItem(item);
                if (!running)
                {
                    return;
                }
            }
        }

        internal Renderer FindRenderer(string meshId)
        {
            return objects.TryGetValue(meshId, out var entry)
                ? entry.Renderer
                : null;
        }

        internal Mesh FindMesh(string meshId)
        {
            if (!objects.TryGetValue(meshId, out var entry))
            {
                return null;
            }

            return geometries.TryGetValue(entry.GeometryId, out var geometry)
                ? geometry.Mesh
                : null;
        }

        internal int ObjectCount => objects.Count;
        internal bool IsRunning => running;
        internal string Status => status;
        internal string Error => error;

        internal void StopForTests()
        {
            StopInternal("Disabled", string.Empty, false);
        }

        internal static string PairTokenKey(string targetHost, int targetPort)
        {
            return $"Malloc.NomadLink.PairToken.{targetHost}:{targetPort}";
        }

        private SessionSnapshot CreateSnapshot(
            bool ownsSession,
            MaterialRowSnapshot[] rows)
        {
            if (!ownsSession)
            {
                return SessionSnapshot.Disabled();
            }

            if (!string.IsNullOrEmpty(error))
            {
                return new SessionSnapshot(
                    true, false, running, status, error,
                    MessageType.Error, rows);
            }

            if (!running)
            {
                return SessionSnapshot.Disabled(true);
            }

            var message = status == "Connected" && rows.Length == 0
                ? "Connected. No synchronized objects."
                : status == "Connected"
                    ? string.Empty
                    : status;
            return new SessionSnapshot(
                true, false, true, status, message,
                MessageType.Info, rows);
        }

        private MaterialRowSnapshot[] CreateMaterialRows()
        {
            return objects.Values
                .Select(entry => new MaterialRowSnapshot(
                    entry.MeshId,
                    entry.State.name,
                    entry.Renderer,
                    entry.Renderer.sharedMaterial))
                .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.MeshId, StringComparer.Ordinal)
                .ToArray();
        }

        private void ProcessWorkerItem(WorkerItem item)
        {
            if (item.Kind == WorkerItemKind.Frame)
            {
                Interlocked.Add(ref queuedBytes, -item.Frame.Size);
                if (!closing)
                {
                    ProcessFrame(item.Frame);
                }

                return;
            }

            if (item.Kind == WorkerItemKind.Connected)
            {
                HandleConnected();
                return;
            }

            var message = !string.IsNullOrEmpty(failureMessage)
                ? failureMessage
                : item.Error;
            StopInternal("Error", message, true);
        }

        private void HandleConnected()
        {
            status = "Waiting for Nomad";
            NotifyInspector();
            var hello = new HelloRequestDto
            {
                type = "hello",
                protocol = ProtocolVersion,
                pair_token = EditorPrefs.GetString(
                    PairTokenKey(host, port), string.Empty),
                bridge_version = BridgeVersion,
                client_name = "Unity",
                capabilities = Capabilities
            };
            Send(hello);
        }

        private void ProcessFrame(Frame frame)
        {
            try
            {
                var json = StrictUtf8.GetString(frame.Json);
                var header = ParseBaseHeader(json, frame.Binary.Length);
                DispatchFrame(json, frame.Binary, header);
            }
            catch (Exception exception)
            {
                BeginProtocolFailure(exception.Message, string.Empty);
            }
        }

        private static BaseDto ParseBaseHeader(string json, int binarySize)
        {
            var trimmed = json.Trim();
            if (trimmed.Length < 2 || trimmed[0] != '{' ||
                trimmed[trimmed.Length - 1] != '}')
            {
                throw new InvalidDataException("The JSON payload is not an object.");
            }

            var header = JsonUtility.FromJson<BaseDto>(json);
            if (header == null || string.IsNullOrEmpty(header.type))
            {
                throw new InvalidDataException("The frame has no message type.");
            }

            ValidateBinarySize(json, binarySize);
            return header;
        }

        private void DispatchFrame(string json, byte[] binary, BaseDto header)
        {
            switch (header.type)
            {
                case "pairing_pending":
                    RequireJsonOnly(binary);
                    SetStatus("Waiting for pairing approval");
                    break;
                case "hello":
                    RequireJsonOnly(binary);
                    HandleHello(json);
                    break;
                case "ping":
                    RequireJsonOnly(binary);
                    Send(new TypeDto { type = "pong" });
                    break;
                case "pong":
                    RequireJsonOnly(binary);
                    break;
                case "error":
                    RequireJsonOnly(binary);
                    HandleRemoteError(json);
                    break;
                case "session_config":
                    RequireJsonOnly(binary);
                    HandleSessionConfig(json);
                    break;
                case "mesh_full":
                    HandleMeshFull(json, binary);
                    break;
                case "mesh_delta":
                    HandleMeshDelta(json, binary);
                    break;
                case "mesh_attributes":
                    HandleMeshAttributes(json, binary);
                    break;
                case "mesh_instance":
                    RequireJsonOnly(binary);
                    HandleMeshInstance(json);
                    break;
                case "object_state":
                    RequireJsonOnly(binary);
                    HandleObjectState(json);
                    break;
                case "object_delete":
                    RequireJsonOnly(binary);
                    HandleObjectDelete(json);
                    break;
            }
        }

        private void HandleHello(string json)
        {
            var reply = JsonUtility.FromJson<HelloReplyDto>(json);
            if (reply == null || reply.protocol != ProtocolVersion)
            {
                throw new InvalidDataException("Nomad uses an unsupported protocol.");
            }

            if (!HasCapability(reply.capabilities, "scene_transfer") ||
                !HasCapability(reply.capabilities, "session_config"))
            {
                throw new InvalidDataException(
                    "Nomad does not provide the required capabilities.");
            }

            if (!string.IsNullOrEmpty(reply.pair_token))
            {
                EditorPrefs.SetString(
                    PairTokenKey(host, port), reply.pair_token);
            }

            helloReceived = true;
            SetStatus("Waiting for session configuration");
        }

        private void HandleRemoteError(string json)
        {
            var remoteError = JsonUtility.FromJson<ErrorDto>(json);
            var message = remoteError == null ||
                string.IsNullOrWhiteSpace(remoteError.message)
                ? "Nomad reported an error."
                : remoteError.message;
            StopInternal("Error", message, true);
        }

        private void HandleSessionConfig(string json)
        {
            RequireHello();
            var config = JsonUtility.FromJson<SessionConfigDto>(json);
            ValidateSessionConfig(json, config);
            UpdateGates(config);

            if (!overrideSent)
            {
                SendSessionOverride(config);
                overrideSent = true;
                SetStatus("Waiting for session configuration");
                return;
            }

            if (!configurationEstablished)
            {
                EstablishConfiguration(config);
                return;
            }

            SetStatus("Connected");
        }

        private void EstablishConfiguration(SessionConfigDto config)
        {
            if (!IsRequestedConfiguration(config))
            {
                SendSessionOverride(config);
                SetStatus("Waiting for session configuration");
                return;
            }

            configurationEstablished = true;
            SetStatus("Connected");
            Send(new TypeDto { type = "request_scene" });
        }

        private void SendSessionOverride(SessionConfigDto config)
        {
            Send(new SetSessionConfigDto
            {
                type = "set_session_config",
                base_revision = config.revision,
                live_sync = true,
                sync_mode = "nomad",
                sync_view = config.sync_view,
                sync_objects = true,
                sync_materials = config.sync_materials,
                sync_lights = config.sync_lights,
                sync_cameras = config.sync_cameras,
                sync_shading = config.sync_shading,
                sync_postprocess = config.sync_postprocess
            });
        }

        private void UpdateGates(SessionConfigDto config)
        {
            liveSync = config.live_sync;
            syncObjects = config.sync_objects;
            activeSource = config.active_source;
        }

        private static bool IsRequestedConfiguration(SessionConfigDto config)
        {
            return config.live_sync && config.sync_objects &&
                config.sync_mode == "nomad" &&
                config.active_source == "nomad";
        }

        private static void ValidateSessionConfig(
            string json,
            SessionConfigDto config)
        {
            if (config == null || !HasRevision(json))
            {
                throw new InvalidDataException(
                    "The session configuration has no revision.");
            }

            if (config.revision < 0 ||
                !IsOneOf(config.sync_mode, "auto", "nomad", "client") ||
                !IsOneOf(config.active_source, "none", "nomad", "client"))
            {
                throw new InvalidDataException(
                    "The session configuration is invalid.");
            }
        }

        private void HandleMeshFull(string json, byte[] binary)
        {
            RequireHello();
            var header = JsonUtility.FromJson<MeshFullDto>(json);
            ValidateMeshFullHeader(json, header);
            var vertices = ReadVertices(
                binary, header.position_offset, header.vertex_count);
            var faces = ReadFaces(
                binary, header.face_offset, header.face_count);
            var data = ReadMeshData(json, binary, vertices, faces);

            if (!CanApplyLiveFrame(json))
            {
                return;
            }

            try
            {
                data.Colors = ReadPaint(json, binary, vertices.Length, null, null);
            }
            catch (Exception exception) when (IsInvalidUpdate(exception))
            {
                SetStatus("Connected. Skipped paint: " + exception.Message);
                return;
            }

            ApplyMeshFull(header, json, data);
            Send(new MeshAckDto
            {
                type = "mesh_ack",
                mesh_id = header.mesh_id,
                request_id = header.request_id
            });
        }

        private void ApplyMeshFull(
            MeshFullDto header,
            string json,
            MeshData data)
        {
            var geometry = GetOrCreateGeometry(header.geometry_id);
            ValidateTopologyReplacement(
                geometry.Data, header.replace_topology, data);
            WriteMesh(geometry.Mesh, data);
            geometry.Data = data;

            var entry = GetOrCreateObject(header.mesh_id);
            AttachGeometry(entry, geometry);
            ApplyIncomingState(entry, json, header.request_id);
        }

        private void HandleMeshInstance(string json)
        {
            RequireHello();
            var header = JsonUtility.FromJson<MeshInstanceDto>(json);
            ValidateMeshInstanceHeader(header);
            if (!CanApplyLiveFrame(json))
            {
                return;
            }

            if (!geometries.TryGetValue(
                header.geometry_id, out var geometry))
            {
                SendRecoverableError(
                    "The instance geometry is unknown.",
                    header.request_id);
                Send(new RequestMeshDto
                {
                    type = "request_mesh",
                    link_id = header.mesh_id
                });
                return;
            }

            var entry = GetOrCreateObject(header.mesh_id);
            AttachGeometry(entry, geometry);
            ApplyIncomingState(entry, json, header.request_id);
        }

        private void HandleMeshDelta(string json, byte[] binary)
        {
            RequireHello();
            if (!CanApplyLiveFrame(json))
            {
                return;
            }

            try
            {
                ApplyMeshDelta(json, binary);
            }
            catch (Exception exception) when (IsInvalidUpdate(exception))
            {
                SetStatus("Connected. Skipped mesh delta: " + exception.Message);
            }
        }

        private void ApplyMeshDelta(string json, byte[] binary)
        {
            var header = ReadUpdateHeader(json, true);
            if (header.index_format != "uint32")
            {
                throw new InvalidDataException("The delta index format is unsupported.");
            }

            if (!TryGetUpdateObject(header.mesh_id, out var entry)) return;
            var geometry = GetGeometry(entry);
            var channels = ReadChannels(json);
            var delta = ReadDelta(binary, header, geometry.Data.Positions.Length,
                channels.HasPosition);
            var colors = ReadPaint(json, binary, header.vertex_count,
                geometry.Data.Colors, delta.Indices);
            ValidateGeometryUsers(entry.GeometryId);
            if (channels.HasPosition)
            {
                ApplyPositionDelta(geometry.Data.Positions, delta.Indices, delta.Positions);
                UpdateMeshPositions(geometry.Mesh, geometry.Data);
                if (geometry.Data.Uvs.Length > 0) geometry.Mesh.RecalculateTangents();
            }

            geometry.Data.Colors = colors;
            if (channels.HasColor || channels.HasOpacity) WriteColors(geometry.Mesh, geometry.Data);
            ApplyIncomingState(entry, json, header.request_id);
        }

        private void HandleMeshAttributes(string json, byte[] binary)
        {
            RequireHello();
            if (!CanApplyLiveFrame(json)) return;
            try
            {
                var header = ReadUpdateHeader(json, false);
                if (!TryGetUpdateObject(header.mesh_id, out var entry)) return;
                var geometry = GetGeometry(entry);
                if (header.vertex_count != geometry.Data.Positions.Length)
                    throw new InvalidDataException("The paint topology does not match.");
                var colors = ReadPaint(json, binary, header.vertex_count,
                    geometry.Data.Colors, null);
                ValidateGeometryUsers(entry.GeometryId);
                geometry.Data.Colors = colors;
                WriteColors(geometry.Mesh, geometry.Data);
            }
            catch (Exception exception) when (IsInvalidUpdate(exception))
            {
                SetStatus("Connected. Skipped mesh attributes: " + exception.Message);
            }
        }

        private bool TryGetUpdateObject(string meshId, out ObjectEntry entry)
        {
            if (objects.TryGetValue(meshId, out entry)) return true;
            Send(new RequestMeshDto { type = "request_mesh", link_id = meshId });
            return false;
        }

        private static bool IsInvalidUpdate(Exception exception)
        {
            return exception is InvalidDataException || exception is OverflowException;
        }

        private void HandleObjectState(string json)
        {
            RequireHello();
            var header = JsonUtility.FromJson<ObjectStateHeaderDto>(json);
            if (header == null || string.IsNullOrEmpty(header.link_id))
            {
                throw new InvalidDataException(
                    "The object state has no link_id.");
            }

            if (!CanApplyLiveFrame(json))
            {
                return;
            }

            if (!objects.TryGetValue(header.link_id, out var entry))
            {
                Send(new RequestMeshDto
                {
                    type = "request_mesh",
                    link_id = header.link_id
                });
                return;
            }

            ApplyIncomingState(entry, json, string.Empty);
        }

        private void HandleObjectDelete(string json)
        {
            RequireHello();
            var header = JsonUtility.FromJson<ObjectDeleteDto>(json);
            if (header == null || string.IsNullOrEmpty(header.link_id))
            {
                throw new InvalidDataException(
                    "The object deletion has no link_id.");
            }

            if (!CanApplyLiveFrame(json))
            {
                return;
            }

            DeleteObject(header.link_id);
        }

        private void ApplyIncomingState(
            ObjectEntry entry,
            string json,
            string requestId)
        {
            if (!ValidateEntry(entry, out var validationError))
            {
                throw new InvalidDataException(validationError);
            }

            var oldName = entry.State.name;
            var result = PrepareObjectState(entry.State, json);
            if (result.IsSheared)
            {
                SendRecoverableError(
                    "Unity cannot apply a sheared object transform.",
                    requestId);
                return;
            }

            entry.State = result.State;
            entry.GameObject.name = result.State.name;
            entry.Renderer.enabled = result.State.visible;
            entry.GameObject.transform.SetPositionAndRotation(
                result.Transform.Position,
                result.Transform.Rotation);
            entry.GameObject.transform.localScale = result.Transform.Scale;
            if (!string.Equals(oldName, result.State.name, StringComparison.Ordinal))
            {
                NotifyInspector();
            }
        }

        internal static StateUpdate PrepareObjectState(
            ObjectStateDto state,
            string json)
        {
            var candidate = state.Clone();
            JsonUtility.FromJsonOverwrite(json, candidate);
            if (candidate.world_matrix == null ||
                candidate.world_matrix.Length != 16)
            {
                throw new InvalidDataException(
                    "The object matrix must contain 16 values.");
            }

            var matrix = ConvertMatrix(candidate.world_matrix);
            var decomposition = Decompose(matrix);
            if (decomposition.Kind == DecompositionKind.Invalid)
            {
                throw new InvalidDataException(
                    "The object matrix contains invalid values.");
            }

            return decomposition.Kind == DecompositionKind.Shear
                ? StateUpdate.Sheared(state)
                : StateUpdate.Applied(candidate, decomposition.Transform);
        }

        private GeometryEntry GetOrCreateGeometry(string geometryId)
        {
            if (geometries.TryGetValue(geometryId, out var geometry))
            {
                return geometry;
            }

            var mesh = new Mesh
            {
                name = geometryId,
                hideFlags = HideFlags.DontSave
            };
            geometry = new GeometryEntry(geometryId, mesh);
            geometries.Add(geometryId, geometry);
            return geometry;
        }

        private ObjectEntry GetOrCreateObject(string meshId)
        {
            if (objects.TryGetValue(meshId, out var entry))
            {
                if (!ValidateEntry(entry, out var validationError))
                {
                    throw new InvalidDataException(validationError);
                }

                return entry;
            }

            var gameObject = new GameObject(meshId)
            {
                hideFlags = HideFlags.DontSave
            };
            gameObject.transform.SetParent(previewRoot.transform, false);
            var filter = gameObject.AddComponent<MeshFilter>();
            var renderer = gameObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = null;
            entry = new ObjectEntry(
                meshId, gameObject, filter, renderer,
                ObjectStateDto.Initial(meshId));
            objects.Add(meshId, entry);
            NotifyInspector();
            return entry;
        }

        private void AttachGeometry(
            ObjectEntry entry,
            GeometryEntry geometry)
        {
            if (entry.GeometryId == geometry.GeometryId)
            {
                entry.Filter.sharedMesh = geometry.Mesh;
                return;
            }

            ReleaseGeometry(entry.GeometryId);
            entry.GeometryId = geometry.GeometryId;
            geometry.ReferenceCount++;
            entry.Filter.sharedMesh = geometry.Mesh;
        }

        private void ReleaseGeometry(string geometryId)
        {
            if (string.IsNullOrEmpty(geometryId) ||
                !geometries.TryGetValue(geometryId, out var geometry))
            {
                return;
            }

            geometry.ReferenceCount--;
            if (geometry.ReferenceCount > 0)
            {
                return;
            }

            geometries.Remove(geometryId);
            UnityEngine.Object.DestroyImmediate(geometry.Mesh);
        }

        private void DeleteObject(string meshId)
        {
            if (!objects.TryGetValue(meshId, out var entry))
            {
                return;
            }

            objects.Remove(meshId);
            ReleaseGeometry(entry.GeometryId);
            UnityEngine.Object.DestroyImmediate(entry.GameObject);
            NotifyInspector();
        }

        private GeometryEntry GetGeometry(ObjectEntry entry)
        {
            if (!geometries.TryGetValue(
                entry.GeometryId, out var geometry))
            {
                throw new InvalidDataException(
                    "The preview geometry is missing.");
            }

            return geometry;
        }

        private void ValidateGeometryUsers(string geometryId)
        {
            foreach (var entry in objects.Values)
            {
                if (entry.GeometryId == geometryId &&
                    !ValidateEntry(entry, out var validationError))
                {
                    throw new InvalidDataException(validationError);
                }
            }
        }

        private bool ValidateAllEntries(out string validationError)
        {
            foreach (var entry in objects.Values)
            {
                if (!ValidateEntry(entry, out validationError))
                {
                    return false;
                }
            }

            validationError = string.Empty;
            return true;
        }

        private bool ValidateEntry(ObjectEntry entry, out string validationError)
        {
            if (entry.GameObject == null || entry.Filter == null ||
                entry.Renderer == null)
            {
                validationError = "A preview renderer is missing.";
                return false;
            }

            if (!geometries.TryGetValue(
                entry.GeometryId, out var geometry) ||
                geometry.Mesh == null ||
                entry.Filter.sharedMesh != geometry.Mesh)
            {
                validationError = "A preview mesh is corrupt.";
                return false;
            }

            validationError = string.Empty;
            return true;
        }

        private static void ValidateMeshFullHeader(
            string json,
            MeshFullDto header)
        {
            if (header == null ||
                string.IsNullOrEmpty(header.mesh_id) ||
                string.IsNullOrEmpty(header.geometry_id) ||
                header.name == null)
            {
                throw new InvalidDataException(
                    "The full mesh identity is invalid.");
            }

            if (header.coordinate_system != "nomad_y_up" ||
                header.position_format != "float32x3" ||
                header.face_format != "int32x4")
            {
                throw new InvalidDataException(
                    "The full mesh format is unsupported.");
            }

            if (header.vertex_count < 0 || header.face_count < 0 ||
                header.world_matrix == null ||
                header.world_matrix.Length != 16 ||
                !HasRequiredSmoothShading(json))
            {
                throw new InvalidDataException(
                    "The full mesh header is invalid.");
            }
        }

        private static void ValidateMeshInstanceHeader(MeshInstanceDto header)
        {
            if (header == null ||
                string.IsNullOrEmpty(header.mesh_id) ||
                string.IsNullOrEmpty(header.geometry_id) ||
                header.name == null ||
                header.world_matrix == null ||
                header.world_matrix.Length != 16)
            {
                throw new InvalidDataException(
                    "The mesh instance header is invalid.");
            }
        }

        private static void ValidateMeshDeltaHeader(MeshDeltaDto header)
        {
            if (header == null || string.IsNullOrEmpty(header.mesh_id) ||
                header.count < 0 || header.vertex_count < 0)
            {
                throw new InvalidDataException(
                    "The mesh delta header is invalid.");
            }
        }

        private static MeshDeltaDto ReadUpdateHeader(string json, bool sparse)
        {
            var header = new MeshDeltaDto();
            var alternate = new MeshDeltaDto { count = -1, vertex_count = -1, index_offset = -1 };
            JsonUtility.FromJsonOverwrite(json, header);
            JsonUtility.FromJsonOverwrite(json, alternate);
            ValidateMeshDeltaHeader(header);
            if (header.vertex_count != alternate.vertex_count ||
                (sparse && (header.count != alternate.count || header.index_offset != alternate.index_offset)))
                throw new InvalidDataException("The mesh update header is incomplete.");
            return header;
        }

        private static Vector3[] ReadVertices(
            byte[] binary,
            long offset,
            int count)
        {
            ValidateRange(offset, count, 12, binary.Length);
            var vertices = new Vector3[count];
            for (var index = 0; index < count; index++)
            {
                var position = checked((int)(offset + index * 12L));
                var vertex = new Vector3(
                    ReadSingle(binary, position),
                    ReadSingle(binary, position + 4),
                    -ReadSingle(binary, position + 8));
                if (!IsFinite(vertex))
                {
                    throw new InvalidDataException(
                        "A vertex is not finite.");
                }

                vertices[index] = vertex;
            }

            return vertices;
        }

        private static int[] ReadFaces(
            byte[] binary,
            long offset,
            int count)
        {
            ValidateRange(offset, count, 16, binary.Length);
            var faces = new int[count * 4];
            for (var face = 0; face < count; face++)
            {
                var position = checked((int)(offset + face * 16L));
                for (var corner = 0; corner < 4; corner++)
                {
                    faces[face * 4 + corner] =
                        BinaryPrimitives.ReadInt32LittleEndian(
                            binary.AsSpan(position + corner * 4, 4));
                }
            }

            return faces;
        }

        private static MeshData ReadMeshData(
            string json, byte[] binary, Vector3[] positions, int[] faces)
        {
            var sourceTriangles = TriangulateFaces(faces, positions.Length);
            var uvHeader = ReadUvHeader(json);
            if (uvHeader == null)
            {
                return new MeshData(positions, sourceTriangles, sourceTriangles,
                    Enumerable.Range(0, positions.Length).ToArray(), Array.Empty<Vector2>());
            }

            var coordinates = ReadUvs(binary, uvHeader, faces.Length);
            return SplitUvVertices(binary, uvHeader.face_uv_offset,
                positions, faces, sourceTriangles, coordinates);
        }

        private static UvDto ReadUvHeader(string json)
        {
            var first = new UvDto();
            var second = new UvDto
            {
                texcoord_count = -1, texcoord_offset = -1,
                face_uv_offset = -1, texcoord_format = "absent"
            };
            JsonUtility.FromJsonOverwrite(json, first);
            JsonUtility.FromJsonOverwrite(json, second);
            var present = new[]
            {
                first.texcoord_count == second.texcoord_count,
                first.texcoord_offset == second.texcoord_offset,
                first.face_uv_offset == second.face_uv_offset,
                first.texcoord_format == second.texcoord_format
            };
            if (!present.Any(value => value))
            {
                return null;
            }

            if (!present.All(value => value))
            {
                throw new InvalidDataException("The UV field group is incomplete.");
            }

            return first;
        }

        private static Vector2[] ReadUvs(byte[] binary, UvDto header, int cornerCount)
        {
            if (header.texcoord_count < 0 || header.texcoord_offset < 0 ||
                header.face_uv_offset < 0 || header.texcoord_format != "float32x2")
            {
                throw new InvalidDataException("The UV field group is incomplete or unsupported.");
            }

            ValidateRange(header.texcoord_offset, header.texcoord_count, 8, binary.Length);
            ValidateRange(header.face_uv_offset, cornerCount, 4, binary.Length);
            var coordinates = new Vector2[header.texcoord_count];
            for (var index = 0; index < coordinates.Length; index++)
            {
                var offset = checked((int)(header.texcoord_offset + index * 8L));
                var u = ReadSingle(binary, offset);
                var v = ReadSingle(binary, offset + 4);
                if (!IsFinite(u) || !IsFinite(v))
                {
                    throw new InvalidDataException("A UV coordinate is not finite.");
                }

                coordinates[index] = new Vector2(u, 1f - v);
            }

            return coordinates;
        }

        private static MeshData SplitUvVertices(
            byte[] binary, long uvOffset, Vector3[] positions, int[] faces,
            int[] sourceTriangles, Vector2[] coordinates)
        {
            var pairs = new Dictionary<(int, int), int>();
            var sources = new List<int>();
            var uvs = new List<Vector2>();
            var splitFaces = new int[faces.Length];
            for (var corner = 0; corner < faces.Length; corner++)
            {
                if (faces[corner] == -1)
                {
                    splitFaces[corner] = -1;
                    continue;
                }

                var uvIndex = BinaryPrimitives.ReadInt32LittleEndian(
                    binary.AsSpan(checked((int)(uvOffset + corner * 4L)), 4));
                if (uvIndex < 0 || uvIndex >= coordinates.Length)
                {
                    throw new InvalidDataException("A face UV index is invalid.");
                }

                var key = (faces[corner], uvIndex);
                if (!pairs.TryGetValue(key, out var output))
                {
                    output = sources.Count;
                    pairs.Add(key, output);
                    sources.Add(faces[corner]);
                    uvs.Add(coordinates[uvIndex]);
                }

                splitFaces[corner] = output;
            }

            return new MeshData(positions, sourceTriangles,
                TriangulateFaces(splitFaces, sources.Count), sources.ToArray(), uvs.ToArray());
        }

        internal static int[] TriangulateFaces(
            int[] faces,
            int vertexCount)
        {
            if (faces == null || faces.Length % 4 != 0)
            {
                throw new InvalidDataException(
                    "The face array is invalid.");
            }

            var quadCount = 0;
            for (var face = 0; face < faces.Length; face += 4)
            {
                ValidateFace(faces, face, vertexCount);
                if (faces[face + 3] >= 0)
                {
                    quadCount++;
                }
            }

            var triangleCount = checked(faces.Length / 4 + quadCount);
            var triangles = new int[checked(triangleCount * 3)];
            var output = 0;
            for (var face = 0; face < faces.Length; face += 4)
            {
                triangles[output++] = faces[face];
                triangles[output++] = faces[face + 2];
                triangles[output++] = faces[face + 1];
                if (faces[face + 3] >= 0)
                {
                    triangles[output++] = faces[face];
                    triangles[output++] = faces[face + 3];
                    triangles[output++] = faces[face + 2];
                }
            }

            return triangles;
        }

        private static void ValidateFace(
            int[] faces,
            int offset,
            int vertexCount)
        {
            for (var corner = 0; corner < 3; corner++)
            {
                if (faces[offset + corner] < 0 ||
                    faces[offset + corner] >= vertexCount)
                {
                    throw new InvalidDataException(
                        "A face index is invalid.");
                }
            }

            var fourth = faces[offset + 3];
            if (fourth < -1 || fourth >= vertexCount)
            {
                throw new InvalidDataException(
                    "A face index is invalid.");
            }
        }

        private static DeltaData ReadDelta(
            byte[] binary,
            MeshDeltaDto header,
            int currentVertexCount,
            bool hasPositions)
        {
            if (header.vertex_count != currentVertexCount)
            {
                throw new InvalidDataException(
                    "The mesh delta topology does not match.");
            }

            ValidateRange(header.index_offset, header.count, 4, binary.Length);
            var indices = new int[header.count];
            var positions = hasPositions
                ? ReadVertices(binary, header.position_offset, header.count)
                : Array.Empty<Vector3>();
            for (var item = 0; item < header.count; item++)
            {
                var indexPosition = checked((int)(
                    header.index_offset + item * 4L));
                var vertexIndex = BinaryPrimitives.ReadUInt32LittleEndian(
                    binary.AsSpan(indexPosition, 4));
                if (vertexIndex >= currentVertexCount)
                {
                    throw new InvalidDataException(
                        "A mesh delta index is invalid.");
                }

                indices[item] = (int)vertexIndex;
            }

            return new DeltaData(indices, positions);
        }

        private static ChannelsDto ReadChannels(string json)
        {
            var first = new ChannelsDto();
            var second = new ChannelsDto
            {
                position_offset = -1, color_offset = -1, opacity_offset = -1,
                position_format = "absent", color_format = "absent", opacity_format = "absent"
            };
            JsonUtility.FromJsonOverwrite(json, first);
            JsonUtility.FromJsonOverwrite(json, second);
            first.HasPosition = ValidateChannel(first.position_offset == second.position_offset,
                first.position_format == second.position_format, first.position_format, "float32x3");
            first.HasColor = ValidateChannel(first.color_offset == second.color_offset,
                first.color_format == second.color_format, first.color_format, "rgbm8");
            first.HasOpacity = ValidateChannel(first.opacity_offset == second.opacity_offset,
                first.opacity_format == second.opacity_format, first.opacity_format, "uint8_norm");
            return first;
        }

        private static bool ValidateChannel(bool offsetPresent, bool formatPresent,
            string format, string supported)
        {
            if (offsetPresent != formatPresent || (formatPresent && format != supported))
                throw new InvalidDataException("A channel is incomplete or its format is unsupported.");
            return offsetPresent;
        }

        private static Color[] ReadPaint(string json, byte[] binary, int vertexCount,
            Color[] current, int[] indices)
        {
            var channels = ReadChannels(json);
            if (!channels.HasColor && !channels.HasOpacity)
                return current ?? Array.Empty<Color>();
            var count = indices?.Length ?? vertexCount;
            if (channels.HasColor) ValidateRange(channels.color_offset, count, 4, binary.Length);
            if (channels.HasOpacity) ValidateRange(channels.opacity_offset, count, 1, binary.Length);
            var colors = current != null && current.Length == vertexCount
                ? (Color[])current.Clone()
                : Enumerable.Repeat(Color.white, vertexCount).ToArray();
            for (var index = 0; index < count; index++)
            {
                var source = indices == null ? index : indices[index];
                var color = colors[source];
                if (channels.HasColor)
                {
                    var offset = checked((int)(channels.color_offset + index * 4L));
                    var multiplier = binary[offset + 3] / 65025f;
                    color.r = binary[offset] * multiplier;
                    color.g = binary[offset + 1] * multiplier;
                    color.b = binary[offset + 2] * multiplier;
                }
                if (channels.HasOpacity)
                    color.a = binary[checked((int)(channels.opacity_offset + index))] / 255f;
                colors[source] = color;
            }
            return colors;
        }

        private static void WriteColors(Mesh mesh, MeshData data)
        {
            mesh.colors = data.Colors.Length == 0 ? Array.Empty<Color>()
                : data.Sources.Select(source => data.Colors[source]).ToArray();
        }

        private static void UpdateMeshPositions(Mesh mesh, MeshData data)
        {
            var vertices = new Vector3[data.Sources.Length];
            var normals = new Vector3[data.Sources.Length];
            var sourceNormals = CalculateSourceNormals(data);
            for (var index = 0; index < vertices.Length; index++)
            {
                vertices[index] = data.Positions[data.Sources[index]];
                normals[index] = sourceNormals[data.Sources[index]];
            }

            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.RecalculateBounds();
        }

        private static Vector3[] CalculateSourceNormals(MeshData data)
        {
            var normals = new Vector3[data.Positions.Length];
            for (var index = 0; index < data.SourceTriangles.Length; index += 3)
            {
                var a = data.SourceTriangles[index];
                var b = data.SourceTriangles[index + 1];
                var c = data.SourceTriangles[index + 2];
                var normal = Vector3.Cross(data.Positions[b] - data.Positions[a],
                    data.Positions[c] - data.Positions[a]);
                normals[a] += normal;
                normals[b] += normal;
                normals[c] += normal;
            }

            for (var index = 0; index < normals.Length; index++)
            {
                normals[index] = normals[index].normalized;
            }

            return normals;
        }

        internal static void ApplyPositionDelta(
            Vector3[] vertices,
            int[] indices,
            Vector3[] positions)
        {
            if (indices.Length != positions.Length)
            {
                throw new InvalidDataException(
                    "The mesh delta arrays do not match.");
            }

            for (var item = 0; item < indices.Length; item++)
            {
                if (indices[item] < 0 || indices[item] >= vertices.Length ||
                    !IsFinite(positions[item]))
                {
                    throw new InvalidDataException(
                        "The mesh delta is invalid.");
                }

                vertices[indices[item]] = positions[item];
            }
        }

        private static void WriteMesh(Mesh mesh, MeshData data)
        {
            mesh.Clear(false);
            mesh.indexFormat = ShouldUseUInt32(data.Sources.Length)
                ? IndexFormat.UInt32
                : IndexFormat.UInt16;
            UpdateMeshPositions(mesh, data);
            mesh.triangles = data.Triangles;
            mesh.uv = data.Uvs;
            WriteColors(mesh, data);
            if (data.Uvs.Length > 0)
            {
                mesh.RecalculateTangents();
            }
        }

        internal static bool ShouldUseUInt32(int vertexCount)
        {
            return vertexCount > 65535;
        }

        private static void ValidateTopologyReplacement(
            MeshData current,
            bool replaceTopology,
            MeshData next)
        {
            if (current == null || replaceTopology)
            {
                return;
            }

            if (current.Positions.Length != next.Positions.Length ||
                !current.SourceTriangles.SequenceEqual(next.SourceTriangles) ||
                !current.Sources.SequenceEqual(next.Sources) ||
                !current.Triangles.SequenceEqual(next.Triangles) ||
                !current.Uvs.SequenceEqual(next.Uvs))
            {
                throw new InvalidDataException(
                    "The full mesh cannot replace topology.");
            }
        }

        internal static Vector3 ConvertPosition(Vector3 value)
        {
            return new Vector3(value.x, value.y, -value.z);
        }

        internal static Matrix4x4 ConvertMatrix(float[] values)
        {
            if (values == null || values.Length != 16 ||
                values.Any(value => !IsFinite(value)))
            {
                throw new InvalidDataException(
                    "The object matrix is invalid.");
            }

            var matrix = new Matrix4x4();
            for (var column = 0; column < 4; column++)
            {
                for (var row = 0; row < 4; row++)
                {
                    matrix[row, column] = values[column * 4 + row];
                }
            }

            var conversion = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
            return conversion * matrix * conversion;
        }

        private static Decomposition Decompose(Matrix4x4 matrix)
        {
            if (!IsAffine(matrix))
            {
                return Decomposition.Invalid();
            }

            var x = (Vector3)matrix.GetColumn(0);
            var y = (Vector3)matrix.GetColumn(1);
            var z = (Vector3)matrix.GetColumn(2);
            var scale = new Vector3(x.magnitude, y.magnitude, z.magnitude);
            if (scale.x <= MatrixTolerance ||
                scale.y <= MatrixTolerance ||
                scale.z <= MatrixTolerance)
            {
                return Decomposition.Invalid();
            }

            x /= scale.x;
            y /= scale.y;
            z /= scale.z;
            if (HasShear(x, y, z))
            {
                return Decomposition.Shear();
            }

            if (Vector3.Dot(Vector3.Cross(x, y), z) < 0f)
            {
                x = -x;
                scale.x = -scale.x;
            }

            var rotation = Quaternion.LookRotation(z, y);
            var position = (Vector3)matrix.GetColumn(3);
            return IsFinite(position) && IsFinite(rotation)
                ? Decomposition.Valid(
                    new TransformData(position, rotation, scale))
                : Decomposition.Invalid();
        }

        private static bool IsAffine(Matrix4x4 matrix)
        {
            return IsFinite(matrix[0, 3]) &&
                IsFinite(matrix[1, 3]) &&
                IsFinite(matrix[2, 3]) &&
                Mathf.Abs(matrix[3, 0]) <= MatrixTolerance &&
                Mathf.Abs(matrix[3, 1]) <= MatrixTolerance &&
                Mathf.Abs(matrix[3, 2]) <= MatrixTolerance &&
                Mathf.Abs(matrix[3, 3] - 1f) <= MatrixTolerance;
        }

        private static bool HasShear(Vector3 x, Vector3 y, Vector3 z)
        {
            return Mathf.Abs(Vector3.Dot(x, y)) > MatrixTolerance ||
                Mathf.Abs(Vector3.Dot(x, z)) > MatrixTolerance ||
                Mathf.Abs(Vector3.Dot(y, z)) > MatrixTolerance ||
                Mathf.Abs(Mathf.Abs(
                    Vector3.Dot(Vector3.Cross(x, y), z)) - 1f) >
                MatrixTolerance;
        }

        private bool CanApplyLiveFrame(string json)
        {
            if (!TryReadRequiredLiveFlag(json, out var isLive))
            {
                throw new InvalidDataException(
                    "The scene frame has no live_sync flag.");
            }

            return !isLive || configurationEstablished &&
                liveSync && syncObjects && activeSource == "nomad";
        }

        private void RequireHello()
        {
            if (!helloReceived)
            {
                throw new InvalidDataException(
                    "Scene data arrived before the Nomad hello.");
            }
        }

        private static void RequireJsonOnly(byte[] binary)
        {
            if (binary.Length != 0)
            {
                throw new InvalidDataException(
                    "The message has an unexpected binary payload.");
            }
        }

        private void SendRecoverableError(string message, string requestId)
        {
            Send(new ErrorDto
            {
                type = "error",
                message = message,
                request_id = requestId
            });
        }

        private void BeginProtocolFailure(string message, string requestId)
        {
            if (closing || !running)
            {
                return;
            }

            closing = true;
            failureMessage = string.IsNullOrWhiteSpace(message)
                ? "The Nomad protocol failed."
                : message;
            status = "Error";
            error = failureMessage;
            NotifyInspector();
            SendRecoverableError(failureMessage, requestId);
            closeAfterWrites = true;
        }

        private void Send(object dto)
        {
            if (!running || stopRequested)
            {
                return;
            }

            var json = JsonUtility.ToJson(dto);
            var frame = EncodeFrame(json, Array.Empty<byte>());
            lock (outgoingLock)
            {
                outgoing.Enqueue(frame);
            }
        }

        private void WorkerRun()
        {
            try
            {
                var localClient = new TcpClient { NoDelay = true };
                client = localClient;
                localClient.Connect(host, port);
                incoming.Enqueue(WorkerItem.Connected());
                RunConnectedWorker(localClient);
            }
            catch (Exception exception)
            {
                if (!stopRequested)
                {
                    incoming.Enqueue(WorkerItem.Stopped(exception.Message));
                }
            }
            finally
            {
                try
                {
                    client?.Close();
                }
                catch (Exception)
                {
                }

                client = null;
            }
        }

        private void RunConnectedWorker(TcpClient localClient)
        {
            using (var stream = localClient.GetStream())
            {
                while (!stopRequested)
                {
                    DrainOutgoing(stream);
                    if (closeAfterWrites && OutgoingIsEmpty())
                    {
                        incoming.Enqueue(WorkerItem.Stopped(failureMessage));
                        return;
                    }

                    if (!localClient.Client.Poll(
                        50000, SelectMode.SelectRead))
                    {
                        continue;
                    }

                    if (localClient.Client.Available == 0)
                    {
                        throw new IOException(
                            "Nomad closed the connection.");
                    }

                    EnqueueFrame(ReadFrame(stream));
                }
            }
        }

        private void DrainOutgoing(NetworkStream stream)
        {
            while (TryDequeueOutgoing(out var frame))
            {
                stream.Write(frame, 0, frame.Length);
            }
        }

        private bool TryDequeueOutgoing(out byte[] frame)
        {
            lock (outgoingLock)
            {
                if (outgoing.Count == 0)
                {
                    frame = null;
                    return false;
                }

                frame = outgoing.Dequeue();
                return true;
            }
        }

        private bool OutgoingIsEmpty()
        {
            lock (outgoingLock)
            {
                return outgoing.Count == 0;
            }
        }

        private void EnqueueFrame(Frame frame)
        {
            var total = Interlocked.Add(ref queuedBytes, frame.Size);
            if (total > QueueLimit)
            {
                Interlocked.Add(ref queuedBytes, -frame.Size);
                throw new InvalidDataException(
                    "The Nomad frame queue is full.");
            }

            incoming.Enqueue(WorkerItem.FromFrame(frame));
        }

        internal static byte[] EncodeFrame(string json, byte[] binary)
        {
            var jsonBytes = StrictUtf8.GetBytes(json);
            if (jsonBytes.Length > JsonLimit || binary.Length > BinaryLimit)
            {
                throw new InvalidDataException("The Nomad frame is too large.");
            }

            var total = checked(8L + jsonBytes.Length + binary.Length);
            if (total > int.MaxValue)
            {
                throw new InvalidDataException("The Nomad frame is too large.");
            }

            var frame = new byte[(int)total];
            BinaryPrimitives.WriteUInt32BigEndian(
                frame.AsSpan(0, 4), (uint)jsonBytes.Length);
            BinaryPrimitives.WriteUInt32BigEndian(
                frame.AsSpan(4, 4), (uint)binary.Length);
            Buffer.BlockCopy(jsonBytes, 0, frame, 8, jsonBytes.Length);
            Buffer.BlockCopy(
                binary, 0, frame, 8 + jsonBytes.Length, binary.Length);
            return frame;
        }

        internal static Frame ReadFrame(Stream stream)
        {
            var prefix = ReadExactly(stream, 8);
            var jsonSize = BinaryPrimitives.ReadUInt32BigEndian(
                prefix.AsSpan(0, 4));
            var binarySize = BinaryPrimitives.ReadUInt32BigEndian(
                prefix.AsSpan(4, 4));
            if (jsonSize > JsonLimit || binarySize > BinaryLimit)
            {
                throw new InvalidDataException("The Nomad frame is too large.");
            }

            var total = checked(8L + jsonSize + binarySize);
            if (total > QueueLimit)
            {
                throw new InvalidDataException("The Nomad frame is too large.");
            }

            return new Frame(
                ReadExactly(stream, checked((int)jsonSize)),
                ReadExactly(stream, checked((int)binarySize)));
        }

        private static byte[] ReadExactly(Stream stream, int size)
        {
            var bytes = new byte[size];
            var offset = 0;
            while (offset < size)
            {
                var count = stream.Read(bytes, offset, size - offset);
                if (count == 0)
                {
                    throw new EndOfStreamException(
                        "Nomad closed the connection.");
                }

                offset += count;
            }

            return bytes;
        }

        private void CreatePreviewRoot()
        {
            previewRoot = new GameObject(PreviewRootName)
            {
                hideFlags = HideFlags.DontSave
            };
            SceneManager.MoveGameObjectToScene(
                previewRoot, owner.gameObject.scene);
        }

        private void SubscribeCallbacks()
        {
            if (callbacksSubscribed)
            {
                return;
            }

            EditorApplication.update += Pump;
            AssemblyReloadEvents.beforeAssemblyReload += OnAssemblyReload;
            EditorApplication.quitting += OnEditorQuit;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            callbacksSubscribed = true;
        }

        private void UnsubscribeCallbacks()
        {
            if (!callbacksSubscribed)
            {
                return;
            }

            EditorApplication.update -= Pump;
            AssemblyReloadEvents.beforeAssemblyReload -= OnAssemblyReload;
            EditorApplication.quitting -= OnEditorQuit;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            callbacksSubscribed = false;
        }

        private void OnAssemblyReload()
        {
            StopInternal("Disabled", string.Empty, false);
        }

        private void OnEditorQuit()
        {
            StopInternal("Disabled", string.Empty, false);
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            StopInternal("Disabled", string.Empty, false);
        }

        private void StopInternal(
            string nextStatus,
            string nextError,
            bool keepOwner)
        {
            stopRequested = true;
            try
            {
                client?.Close();
            }
            catch (Exception)
            {
            }

            if (worker != null && worker != Thread.CurrentThread)
            {
                worker.Join();
            }

            worker = null;
            client = null;
            UnsubscribeCallbacks();
            DestroyPreview();
            ClearQueues();
            ResetProtocolState();
            running = false;
            closing = false;
            closeAfterWrites = false;
            status = nextStatus;
            error = nextError ?? string.Empty;
            if (!keepOwner)
            {
                owner = null;
            }

            NotifyInspector();
        }

        private void DestroyPreview()
        {
            foreach (var entry in objects.Values)
            {
                if (entry.GameObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(entry.GameObject);
                }
            }

            objects.Clear();
            foreach (var geometry in geometries.Values)
            {
                if (geometry.Mesh != null)
                {
                    UnityEngine.Object.DestroyImmediate(geometry.Mesh);
                }
            }

            geometries.Clear();
            if (previewRoot != null)
            {
                UnityEngine.Object.DestroyImmediate(previewRoot);
            }

            previewRoot = null;
        }

        private void ClearQueues()
        {
            while (incoming.TryDequeue(out _))
            {
            }

            Interlocked.Exchange(ref queuedBytes, 0);
            lock (outgoingLock)
            {
                outgoing.Clear();
            }
        }

        private void ResetProtocolState()
        {
            helloReceived = false;
            overrideSent = false;
            configurationEstablished = false;
            liveSync = false;
            syncObjects = false;
            activeSource = "none";
            failureMessage = string.Empty;
        }

        private void SetStoppedError(string message)
        {
            status = "Error";
            error = message;
            running = false;
            NotifyInspector();
        }

        private void SetStatus(string value)
        {
            if (status == value)
            {
                return;
            }

            status = value;
            NotifyInspector();
        }

        private void NotifyInspector()
        {
            InspectorStateChanged?.Invoke();
        }

        private static bool IsValidController(NomadLinkScene scene)
        {
            return scene != null && scene.isActiveAndEnabled &&
                scene.gameObject.scene.IsValid() &&
                scene.gameObject.scene.isLoaded;
        }

        private static bool HasCapability(string[] values, string capability)
        {
            return values != null && Array.IndexOf(values, capability) >= 0;
        }

        private static bool IsOneOf(string value, params string[] options)
        {
            return value != null && Array.IndexOf(options, value) >= 0;
        }

        private static bool TryReadRequiredLiveFlag(
            string json,
            out bool value)
        {
            var first = new LiveDto { live_sync = false };
            var second = new LiveDto { live_sync = true };
            JsonUtility.FromJsonOverwrite(json, first);
            JsonUtility.FromJsonOverwrite(json, second);
            value = first.live_sync;
            return first.live_sync == second.live_sync;
        }

        private static bool HasRequiredSmoothShading(string json)
        {
            var first = new SmoothDto { smooth_shading = false };
            var second = new SmoothDto { smooth_shading = true };
            JsonUtility.FromJsonOverwrite(json, first);
            JsonUtility.FromJsonOverwrite(json, second);
            return first.smooth_shading == second.smooth_shading;
        }

        private static bool HasRevision(string json)
        {
            var first = new SessionConfigDto { revision = int.MinValue };
            var second = new SessionConfigDto { revision = int.MaxValue };
            JsonUtility.FromJsonOverwrite(json, first);
            JsonUtility.FromJsonOverwrite(json, second);
            return first.revision == second.revision;
        }

        private static void ValidateBinarySize(string json, int actualSize)
        {
            var first = new BinarySizeDto { binary_size = -1 };
            var second = new BinarySizeDto { binary_size = -2 };
            JsonUtility.FromJsonOverwrite(json, first);
            JsonUtility.FromJsonOverwrite(json, second);
            var present = first.binary_size == second.binary_size;
            if ((!present && actualSize != 0) ||
                present && first.binary_size != actualSize)
            {
                throw new InvalidDataException(
                    "The binary_size field does not match the payload.");
            }
        }

        private static void ValidateRange(
            long offset,
            long count,
            long stride,
            int payloadLength)
        {
            if (offset < 0 || count < 0 || stride < 0)
            {
                throw new InvalidDataException(
                    "A binary range is invalid.");
            }

            long end;
            try
            {
                end = checked(offset + checked(count * stride));
            }
            catch (OverflowException)
            {
                throw new InvalidDataException(
                    "A binary range overflows.");
            }

            if (end > payloadLength)
            {
                throw new InvalidDataException(
                    "A binary range exceeds the payload.");
            }
        }

        private static float ReadSingle(byte[] bytes, int offset)
        {
            var bits = BinaryPrimitives.ReadInt32LittleEndian(
                bytes.AsSpan(offset, 4));
            return BitConverter.Int32BitsToSingle(bits);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(Quaternion value)
        {
            return IsFinite(value.x) && IsFinite(value.y) &&
                IsFinite(value.z) && IsFinite(value.w);
        }

        [Serializable]
        internal sealed class ObjectStateDto
        {
            public string name;
            public bool visible = true;
            public float[] world_matrix = IdentityMatrix();

            internal static ObjectStateDto Initial(string initialName)
            {
                return new ObjectStateDto { name = initialName };
            }

            internal ObjectStateDto Clone()
            {
                return new ObjectStateDto
                {
                    name = name,
                    visible = visible,
                    world_matrix = (float[])world_matrix.Clone()
                };
            }

            private static float[] IdentityMatrix()
            {
                return new[]
                {
                    1f, 0f, 0f, 0f,
                    0f, 1f, 0f, 0f,
                    0f, 0f, 1f, 0f,
                    0f, 0f, 0f, 1f
                };
            }
        }

        internal readonly struct SessionSnapshot
        {
            internal SessionSnapshot(
                bool ownsSession,
                bool otherOwner,
                bool enabled,
                string status,
                string message,
                MessageType messageType,
                MaterialRowSnapshot[] rows)
            {
                OwnsSession = ownsSession;
                OtherOwner = otherOwner;
                Enabled = enabled;
                Status = status;
                Message = message;
                MessageType = messageType;
                Rows = rows;
            }

            internal bool OwnsSession { get; }
            internal bool OtherOwner { get; }
            internal bool Enabled { get; }
            internal string Status { get; }
            internal string Message { get; }
            internal MessageType MessageType { get; }
            internal MaterialRowSnapshot[] Rows { get; }

            internal static SessionSnapshot OtherScene()
            {
                return new SessionSnapshot(
                    false, true, false, "Unavailable",
                    "Another scene owns the Nomad Link session.",
                    MessageType.Warning,
                    Array.Empty<MaterialRowSnapshot>());
            }

            internal static SessionSnapshot Disabled(bool ownsSession = false)
            {
                return new SessionSnapshot(
                    ownsSession, false, false, "Disabled",
                    "Enable sync to connect to Nomad.",
                    MessageType.Info,
                    Array.Empty<MaterialRowSnapshot>());
            }
        }

        internal readonly struct MaterialRowSnapshot
        {
            internal MaterialRowSnapshot(
                string meshId,
                string name,
                Renderer renderer,
                Material material)
            {
                MeshId = meshId;
                Name = name;
                Renderer = renderer;
                Material = material;
            }

            internal string MeshId { get; }
            internal string Name { get; }
            internal Renderer Renderer { get; }
            internal Material Material { get; }
        }

        internal readonly struct StateUpdate
        {
            private StateUpdate(
                ObjectStateDto state,
                TransformData transform,
                bool isSheared)
            {
                State = state;
                Transform = transform;
                IsSheared = isSheared;
            }

            internal ObjectStateDto State { get; }
            internal TransformData Transform { get; }
            internal bool IsSheared { get; }

            internal static StateUpdate Applied(
                ObjectStateDto state,
                TransformData transform)
            {
                return new StateUpdate(state, transform, false);
            }

            internal static StateUpdate Sheared(ObjectStateDto state)
            {
                return new StateUpdate(state, default, true);
            }
        }

        internal readonly struct TransformData
        {
            internal TransformData(
                Vector3 position,
                Quaternion rotation,
                Vector3 scale)
            {
                Position = position;
                Rotation = rotation;
                Scale = scale;
            }

            internal Vector3 Position { get; }
            internal Quaternion Rotation { get; }
            internal Vector3 Scale { get; }
        }

        internal sealed class Frame
        {
            internal Frame(byte[] json, byte[] binary)
            {
                Json = json;
                Binary = binary;
                Size = checked(8L + json.Length + binary.Length);
            }

            internal byte[] Json { get; }
            internal byte[] Binary { get; }
            internal long Size { get; }
        }

        private sealed class ObjectEntry
        {
            internal ObjectEntry(
                string meshId,
                GameObject gameObject,
                MeshFilter filter,
                MeshRenderer renderer,
                ObjectStateDto state)
            {
                MeshId = meshId;
                GameObject = gameObject;
                Filter = filter;
                Renderer = renderer;
                State = state;
            }

            internal string MeshId { get; }
            internal string GeometryId { get; set; }
            internal GameObject GameObject { get; }
            internal MeshFilter Filter { get; }
            internal MeshRenderer Renderer { get; }
            internal ObjectStateDto State { get; set; }
        }

        private sealed class GeometryEntry
        {
            internal GeometryEntry(string geometryId, Mesh mesh)
            {
                GeometryId = geometryId;
                Mesh = mesh;
            }

            internal string GeometryId { get; }
            internal Mesh Mesh { get; }
            internal MeshData Data { get; set; }
            internal int ReferenceCount { get; set; }
        }

        private sealed class MeshData
        {
            internal MeshData(Vector3[] positions, int[] sourceTriangles,
                int[] triangles, int[] sources, Vector2[] uvs)
            {
                Positions = positions;
                SourceTriangles = sourceTriangles;
                Triangles = triangles;
                Sources = sources;
                Uvs = uvs;
            }

            internal Vector3[] Positions { get; }
            internal int[] SourceTriangles { get; }
            internal int[] Triangles { get; }
            internal int[] Sources { get; }
            internal Vector2[] Uvs { get; }
            internal Color[] Colors { get; set; } = Array.Empty<Color>();
        }

        private readonly struct DeltaData
        {
            internal DeltaData(int[] indices, Vector3[] positions)
            {
                Indices = indices;
                Positions = positions;
            }

            internal int[] Indices { get; }
            internal Vector3[] Positions { get; }
        }

        private readonly struct Decomposition
        {
            private Decomposition(
                DecompositionKind kind,
                TransformData transform)
            {
                Kind = kind;
                Transform = transform;
            }

            internal DecompositionKind Kind { get; }
            internal TransformData Transform { get; }

            internal static Decomposition Valid(TransformData transform)
            {
                return new Decomposition(DecompositionKind.Valid, transform);
            }

            internal static Decomposition Shear()
            {
                return new Decomposition(DecompositionKind.Shear, default);
            }

            internal static Decomposition Invalid()
            {
                return new Decomposition(DecompositionKind.Invalid, default);
            }
        }

        private enum DecompositionKind
        {
            Valid,
            Shear,
            Invalid
        }

        private sealed class WorkerItem
        {
            private WorkerItem(
                WorkerItemKind kind,
                Frame frame,
                string error)
            {
                Kind = kind;
                Frame = frame;
                Error = error;
            }

            internal WorkerItemKind Kind { get; }
            internal Frame Frame { get; }
            internal string Error { get; }

            internal static WorkerItem Connected()
            {
                return new WorkerItem(
                    WorkerItemKind.Connected, null, string.Empty);
            }

            internal static WorkerItem FromFrame(Frame frame)
            {
                return new WorkerItem(
                    WorkerItemKind.Frame, frame, string.Empty);
            }

            internal static WorkerItem Stopped(string error)
            {
                return new WorkerItem(
                    WorkerItemKind.Stopped, null,
                    string.IsNullOrEmpty(error)
                        ? "The connection stopped."
                        : error);
            }
        }

        private enum WorkerItemKind
        {
            Connected,
            Frame,
            Stopped
        }

        [Serializable]
        private class TypeDto
        {
            public string type;
        }

        [Serializable]
        private sealed class BaseDto : TypeDto
        {
        }

        [Serializable]
        private sealed class HelloRequestDto : TypeDto
        {
            public int protocol;
            public string pair_token;
            public string bridge_version;
            public string client_name;
            public string[] capabilities;
        }

        [Serializable]
        private sealed class HelloReplyDto : TypeDto
        {
            public int protocol;
            public string pair_token;
            public string[] capabilities;
        }

        [Serializable]
        private sealed class ErrorDto : TypeDto
        {
            public string message;
            public string request_id;
        }

        [Serializable]
        private sealed class MeshAckDto : TypeDto
        {
            public string mesh_id;
            public string request_id;
        }

        [Serializable]
        private sealed class RequestMeshDto : TypeDto
        {
            public string link_id;
        }

        [Serializable]
        private sealed class SessionConfigDto : TypeDto
        {
            public int revision;
            public bool live_sync;
            public string sync_mode;
            public string active_source;
            public bool sync_view;
            public bool sync_objects;
            public bool sync_materials;
            public bool sync_lights;
            public bool sync_cameras;
            public bool sync_shading;
            public bool sync_postprocess;
        }

        [Serializable]
        private sealed class SetSessionConfigDto : TypeDto
        {
            public int base_revision;
            public bool live_sync;
            public string sync_mode;
            public bool sync_view;
            public bool sync_objects;
            public bool sync_materials;
            public bool sync_lights;
            public bool sync_cameras;
            public bool sync_shading;
            public bool sync_postprocess;
        }

        [Serializable]
        private sealed class MeshFullDto : TypeDto
        {
            public string mesh_id;
            public string geometry_id;
            public string name;
            public int vertex_count;
            public int face_count;
            public string coordinate_system;
            public float[] world_matrix;
            public long position_offset;
            public string position_format;
            public long face_offset;
            public string face_format;
            public bool replace_topology;
            public string request_id;
        }

        [Serializable]
        private sealed class UvDto
        {
            public int texcoord_count;
            public long texcoord_offset;
            public string texcoord_format;
            public long face_uv_offset;
        }

        [Serializable]
        private sealed class ChannelsDto
        {
            public long position_offset;
            public string position_format;
            public long color_offset;
            public string color_format;
            public long opacity_offset;
            public string opacity_format;
            internal bool HasPosition;
            internal bool HasColor;
            internal bool HasOpacity;
        }

        [Serializable]
        private sealed class MeshInstanceDto : TypeDto
        {
            public string mesh_id;
            public string geometry_id;
            public string name;
            public float[] world_matrix;
            public string request_id;
        }

        [Serializable]
        private sealed class MeshDeltaDto : TypeDto
        {
            public string mesh_id;
            public int count;
            public int vertex_count;
            public long index_offset;
            public string index_format;
            public long position_offset;
            public string position_format;
            public string request_id;
        }

        [Serializable]
        private sealed class ObjectStateHeaderDto : TypeDto
        {
            public string link_id;
        }

        [Serializable]
        private sealed class ObjectDeleteDto : TypeDto
        {
            public string link_id;
        }

        [Serializable]
        private sealed class LiveDto
        {
            public bool live_sync;
        }

        [Serializable]
        private sealed class SmoothDto
        {
            public bool smooth_shading;
        }

        [Serializable]
        private sealed class BinarySizeDto
        {
            public long binary_size;
        }
    }
}
