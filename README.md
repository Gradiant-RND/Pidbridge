PIDBRIDGE MECHANICAL
Version 1.2.9
===================

WHAT PIDBRIDGE DOES
------------------
PidBridge is a Windows AutoCAD plug-in for exchanging complete P&ID drawings
with an AI assistant or another JSON editing tool. It exports a saved DWG to
JSON, then imports an edited JSON file to create and open a new DWG.

The JSON contains both an editable inventory of drawing entities and an
embedded copy of the original DWG. This allows PidBridge to preserve native
CAD objects while applying supported changes. Mechanical features also create
native part references, BOM entries and associated balloons.

PidBridge does not call an AI service itself. You give the exported JSON and
your instructions to an external assistant, then import the returned file.

Typical workflow:

    Saved DWG -> PIDEXPORT -> Complete JSON -> AI/tool edits
              -> PIDIMPORT -> Preview -> New DWG


REQUIREMENTS AND LOADING
------------------------
The current project targets Windows x64 and .NET 10. Its default Autodesk
assembly references point to:

    C:\Program Files\Autodesk\AutoCAD 2025

Native BOM and balloon operations require AutoCAD Mechanical and its COM
interfaces. The code currently uses the version-25 Mechanical COM identifiers.
Compatibility with other Autodesk releases must be checked separately.

To use the plug-in:

1. Start AutoCAD Mechanical and open your DWG.
2. Run NETLOAD and select PidBridgeMechanical.dll from your build or release.
3. If Autodesk blocks loading, check its trusted-location and security settings
   for the plug-in folder.
4. Check the loading message, or run PIDVERSIONDISEGNO9 to display the version
   and the full path of the loaded DLL.

The plug-in's current dialogs and command messages are in Italian.


AVAILABLE COMMANDS
------------------
PIDEXPORT
    Exports the entire saved drawing to a complete JSON file.

PIDIMPORT
    Validates an edited complete JSON, shows a change summary, and creates a
    new DWG under a new filename.

Use PIDEXPORT and PIDIMPORT for the current workflow. Some validation messages
still mention older versioned export command names.


STEP 1 - EXPORT THE DRAWING
--------------------------
1. Save the DWG with _QSAVE. The drawing must exist on disk and have no unsaved
   changes.
2. Run PIDEXPORT.
3. Choose a JSON destination. The suggested name is <drawing>-originale.json.
4. Wait for the export success message.

PidBridge reads the saved DWG, embeds its bytes in the JSON, and inventories
entities in model space, layouts and block definitions. The export does not
modify the open drawing. If the DWG changes during reading, export stops so
that you can complete saving and try again.


STEP 2 - EDIT THE COMPLETE JSON
------------------------------
Give the exported file to your AI assistant or editing tool with a precise
description of the changes. Request a complete modified JSON file suitable
for PIDIMPORT.

For example:

    "Use this PidBridge export to copy the specified component to the requested
    position and assign its new KKS. Return the complete final drawing JSON.
    Preserve native, owners, layers and all unrelated entities. If the copy
    needs BOM data and a balloon, use native Mechanical part references and
    balloons linked to the copied geometry."

The entities array describes the desired final drawing state:

* Keep an entity unchanged to retain it.
* Edit supported fields to change an existing entity.
* Add an entity with a new ID and a valid sourceHandle/copyGroup to copy a
  native source object.
* Add a supported entity without a sourceHandle to create new geometry.
* Remove an entity from the array to request its deletion.

Every unrelated entity must remain in the file. Omitting entities is a
deletion request, not a way to send only the changed portion of the drawing.


STEP 3 - IMPORT AND CREATE A NEW DWG
----------------------------------
1. Run PIDIMPORT and select the complete edited JSON.
2. Review the summary of unchanged, modified, copied, created and removed
   entities, together with the Mechanical part-reference and native-copy counts.
3. Select "Crea il nuovo DWG" to continue, or "Annulla" to cancel.
4. Choose a new DWG filename. The suggested name is <drawing>_modificato.dwg.
   Existing files cannot be overwritten by the importer.
5. Wait for the final success message and inspect the opened result in CAD.

Import reconstructs a temporary drawing from the embedded original DWG,
compares the JSON with that baseline, and applies the changes. When requested,
it opens the temporary drawing in Mechanical to create part references and
balloons, checks their BOM associations, saves the result, then reopens it for
final verification.

The original DWG is not overwritten. Import uses the embedded DWG as its base;
changes made later in another open drawing are not merged into the result.
For another editing cycle, save and export the drawing you want to use next.


HOW THE JSON WORKS
------------------
The format identifier is "pidbridge.full-drawing", with protocol version 1.
This protocol version is separate from the plug-in version.

Main fields:

    format, version     Identify the complete drawing protocol.
    drawingId           Identifies the embedded drawing baseline.
    drawingName         Source drawing name.
    originalPath        Source path, also used to suggest an output folder.
    units               Drawing units; must match the embedded baseline.
    native              Original DWG encoded as Base64, plus its SHA-256 hash.
    owners              Model/layout spaces and block definitions.
    layers              Layer inventory from the native baseline.
    entities            Complete desired entity inventory.
    notes               Export guidance and drawing-specific information.

Entity records include identity, owner, type, layer, appearance, geometry,
attributes and a 4x4 affine transform. A sourceHandle identifies a native
source object. A copyGroup groups copies so native associations can be cloned
together. Bounds are informational; CAD recalculates the actual extents.

Editing rules:

* Preserve native exactly, including its Base64 content and hash.
* Preserve owners and layers, and keep drawingId and units consistent.
* For existing entities, preserve their ID, sourceHandle, ownerId and type.
* Preserve geometry field names and attribute tags/order for existing entities
  and source-based copies. Supported values may be edited.
* Use unique IDs for new entities and valid references to existing owners,
  layers and source objects.
* Do not replace the complete file with instructions, an operations list or a
  partial entity selection. Unknown JSON fields and duplicate property names
  are rejected.


SUPPORTED GEOMETRY AND NATIVE OBJECTS
-----------------------------------
New basic geometry can be created as Line, Polyline, DBText, MText, Circle,
Arc or Solid. Supported existing geometry can be edited; existing block
references also support changes such as placement and attribute values.
Changing a block reference to a different block definition is not supported:
copy a suitable source block instead.

Some objects, including hatches and specialized Mechanical objects, are
represented with geometry.nativeOnly=true. Their full data remains in the
embedded DWG. Preserve their geometry and use native copying and transforms
for supported placement changes. Do not invent an editable replacement for
their native geometry. Direct attribute-definition editing is unsupported.

Entities inside external reference definitions cannot be edited, deleted or
used as sources for local copies. Xrefs, images and fonts remain external
resources and must stay available; the JSON is not a resource packaging tool.


MECHANICAL PART REFERENCES, BOM AND BALLOONS
------------------------------------------
Exported native part references include a mechanicalPart inventory containing
their position, scale, quantity and BOM columns. Treat this as source data.

For a new component requiring BOM integration, use a new entity of type
MechanicalPartReference. Its geometry specifies the sourcePartHandle, complete
BOM data, position, scale, quantity and targetId of the associated geometry.
Preserve all source BOM columns and assign a nonempty, unique KKS. The target
geometry and the reference must belong to the same owner.

For a new MechanicalBalloon, specify a valid sourceBalloonHandle, the target
partReferenceId and an offset. PidBridge copies the source balloon, replaces
its inherited BOM association, attaches it to the target and attempts to move
the balloon head clear of detected obstacles. Import stops if the required
placement or verification cannot be completed.

The importer checks requested KKS values and quantities against the MAIN BOM,
and verifies balloon associations. Legacy Infopoint blocks are not native
Mechanical part references; new Infopoint entities are rejected during import.


LIMITS AND TROUBLESHOOTING
-------------------------
Current protocol limits:

    Embedded DWG:       64 MiB maximum
    Complete JSON:     128 MiB maximum
    Entity inventory:  200,000 entities maximum
    JSON depth:         48 maximum

Export asks you to save first:
    Save the DWG with _QSAVE, wait for completion, then retry PIDEXPORT.

JSON validation fails:
    Check that the file is complete, native is intact, and IDs, references,
    geometry fields and BOM data satisfy the rules above.

Layer validation fails:
    Inspect the reported layer names. If the DWG layer table is damaged, run
    _AUDIT on a copy of the DWG, save it, and export again.

Mechanical operations fail:
    Check that you are running the appropriate AutoCAD Mechanical environment,
    that the source references/balloons exist, and that the drawing supports
    the required MAIN BOM and KKS data.

Import stops after writing a DWG:
    A result may be retained for diagnosis if reopening or verification fails.
    Its presence does not mean import succeeded. Require the final success
    message before considering it verified.

Diagnostic log:

    %TEMP%\PidBridge\import-<AutoCAD-process-id>.log

The log includes import phases, file paths and exception details. Messages are
also sent to the command line, with alerts for errors or unavailable editors.


BUILDING FROM SOURCE
--------------------
Install the .NET 10 SDK and the Autodesk managed assemblies required by the
project. From this directory, build with:

    dotnet build PidBridge.csproj -c Release

If your Autodesk installation is elsewhere, override AutoCADDir:

    dotnet build PidBridge.csproj -c Release -p:AutoCADDir="D:\Autodesk\AutoCAD 2025"

The main output is:

    bin\Release\net10.0-windows\PidBridgeMechanical.dll

The repository includes standalone checks that do not require loading AutoCAD:

    dotnet run --project tests\ProtocolChecks\ProtocolChecks.csproj -c Release
    dotnet run --project tests\FeedbackChecks\FeedbackChecks.csproj -c Release

These checks cover protocol/file handling, balloon placement logic and feedback
behavior. Full DWG and Mechanical COM integration still requires validation
inside the Autodesk application.
