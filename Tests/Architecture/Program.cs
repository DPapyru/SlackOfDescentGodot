using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

using var stream = File.OpenRead(args[0]);
using var pe = new PEReader(stream);
var metadata = pe.GetMetadataReader();
bool diagnostics = args[1] == "diagnostics";
var types = metadata.TypeDefinitions.Select(handle => metadata.GetTypeDefinition(handle)).ToArray();
var names = types.Select(type => metadata.GetString(type.Name)).ToHashSet();
foreach (string required in new[] { "GmWorld", "GameHost", "NativeModManager", "ModContext", "ActorContext", "ContentRegistry", "NativeMapRuntime", "NativeCharacterPreview", "NativeActionEditor", "NativeMotionEditor", "MotionGraph", "MotionPlayer", "NativeMotionService" })
    if (!names.Contains(required)) throw new Exception("Missing compatible type: " + required);
foreach (string name in new[] { "MotionProbe", "NativeModsAudit", "NativeModSdkAudit", "NativeMapAudit", "NativeCharacterAuditRunner", "NativeMotionAudit" })
    if (names.Contains(name) != diagnostics) throw new Exception("Unexpected diagnostic type: " + name);
var methods = types.SelectMany(type => type.GetMethods()).Select(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name)).ToArray();
foreach (string name in new[] { "AuditGraphics", "RunUiAudit", "AuditResourceImages", "AuditResourceSounds", "RunPanBoundaryAudit", "AuditStroke" })
    if (methods.Contains(name) != diagnostics) throw new Exception("Unexpected diagnostic entry: " + name);
Console.WriteLine("MODULE_BUILD_OK " + args[1]);
