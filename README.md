# LithTechLokiResearch

This repo collects materials for researching the LithTech 5 engine, also called "Loki" since it was used in game F.E.A.R 2 which had that internal codename. It was also used in Condemned 2, which was not released for PC though.

All materials are provided without warranty or liability, and imply no legal rights as to what they may or may not be used for, which is entirely the responsibility of their users.

Credits go to Amphos, on whose work and exhaustive research into LithTech much of this is based and without which it would not have been possible: https://github.com/Five-Damned-Dollarz/io_scene_jupex

Some other parts are based on https://github.com/burmaraider/JupiterEX_DatabaseExtractor (Game DB) and work done by Luigi Auriemma (QuickBMS) and people at the no-longer-existant XeNTaX forums (Archives, Bundles...). Thanks as well!

## Contents

This material is research-level. Parts are 010 editor templates, which are useful for studying world (.wld) and other files directly. Other parts are code show examples an how to parse the files and render the world. Those should be treated as pseudo-code, as the will **not** compile directly. (They have been taken out of another project with too many dependencies to easily include here, but they should serve their purpose.)

## Important Terms

- **World** is basically a map, represented by a .wld file. Some parts are sourced out inside a .WldSrvr and some inside a .WldClnt file in the same folder. The actual meshes and assets are inside numerous bundle (.bndl) files, also in the same folder, and refernced from inside the world file.
- **BSPs** and **WorldModels** are basically the same things and refer to a collection of geometry forming a logical entity. Most of them have polygons and "simple" vertices (position-only, not textured) and are used for physics and collision detection and for performing actions when hit, but there are also some without. WorldModels *may* define one or more names, in which case they are linked to *objects* of the same name, providing additional absolute position information to them.
- **Objects** are logical entities defined by a position, rotation, and potentially other properties. Some are linked to a WorldModel, in which case they define additional properties for that model. Others are just points in space (e.g. waypoints) or trigger areas. In Loki, objects have one basic part in the server world (.WldSrvr) file, and another part in the GameDB file of a world.
- **GameDB** is a database of categories and records. It's use for global attributes in the game, like a structured knowledge base, but also there is one for each map containing object properties (something that was inside the world file in earlier versions of LithTech).
- **WorldMeshes** define the visual representation of the world for rendering, split up into useful "blocks". They consist of **RenderFaces** (similar to BSP polygons), but with more complex vertex types and materials. They often define the same or similar geometry as the WorldModels, but while the former are for physics, these are for visualization. The separation is partly because the ones are more a server thing, while the other is more a client task, but also because they can receive different optimizations. In Loki, each world has usually one "global" mesh that is a bit special, and several additional meshes (usually around 10).
- **RenderNodes** define a (flat?) tree that arranges RenderFaces in a tree-like structure that may differ a bit from that defined by the WorldMeshes. In fact a RenderFace could be part of several RenderNodes, plus there are some empty RenderNodes. RenderNodes also define a few additional vectors; possibly for transition portals, but not sure.
- **Instances** or **Prefabs** again refer to more or less the same concept. While the WorldMeshes cover about 70% of the world geometry, a lot would be missing without instances. They seem to cover both the dynamic aspect, as well as repetion of geometry. Basically, additional meshes are defined in prefab (.inst) files (inside world bundles), also with their own "nodes". At certain places, the world points to these prefab nodes, which means they should be rendered at this place of the world.
	- This happens (1) in the "global instances list" of a world (which *always* refers to an external prefab mesh and provides *absolute* coordinates for the instance), and (2) in the "WorldModel instances list" (which lists instances per WM and expetcts the *WM's coordinates* to be used for placement).
	- The WM instances list *can* also point to faces, which also makes faces re-useable. This is basically just done for faces in the global mesh.
	- Both external prefabs, as well as faces, usually don't have absolute coordinates (since they can be renderd in multiple locations) - they are usually centered around the coordinate origin. It's the WM that provides the coordinates through its linked objects (which define the actual, absolute positions - remember there can be multiple linked objects).
	- There are still some points not totally understood about the WM instances list, see also below:
		1. Some entries in this list contain references to prefab files *with* coordinates, but those coordinates are not absolute and don't appear to make sense when applied.
		2. Some other entries (both refering to faces, as well as to external prefab files) in the WM instances list are part of a WM that has *no* linked objects (identifiable by also not having a name) and therefore *no* position information. It's unclear where they should be placed (in absolute terms).
	- Take care when resolving indexes to prefab nodes, as they are not numerically indexes as in the .inst file, but again mapped to another nodes list via their name hash as defined in the .wld file (see the example code).
- **Sectors** and **Portals** are concepts used to optimize performance and calculations e.g. for lighting and possibly AI. They don't seem to be essential for rendering. (Although not sure about skyportals...)

## Current Status

It is possible to render the basic world meshes, with like 5-10% of the geomtry still at the wrong position (see below).

Some of the things that are not yet supported:

- There may be a few instances which are "floating" or have otherwise off position assigned. This needs to be investigated.
- Some prefabs and faces have multiple "versions" of the same face defined (e.g. broken and intact). Currently, they are all rendered as it's not clear how they are distinguished (probably via a set of flags that has not been researched well yet).
- Rendering of external models (.mdl files) linked to objects.
- Animated materials (.txanim) are not parsed.
- Lightmaps, probably implemented via cube maps / texture probes in LT5, are not considered for now.

## Placement of instances without absolute coordinates

This is the biggest obvious issue at the moment and is therefore again described separately here. It seems to apply mainly to "layered" materials (e.g. additive shaders, grime, light halos etc.).

There are also multiple ways instances (see above) are linked into the rendering process & placed on the map, depending on their type:
- Faces are usually referred to by RenderNodes (which in turn are referred to by BSPs in non-unique way).
  A series of faces in the *global* mesh (index 0) can also be referenced to directly by BSPs in the BSP render instances list (a type 0 ref).
- Prefabs are partially placed via the global prefab list (usually their first render node only).
  Parts of prefabs can also be placed via the BSP render instances list (type 1 ref) *or* a PrefabPlacement entry in the same list.
- RenderNode faces use absolute positions, and the global prefab list has their own position and rotation specs.
  All of the other seem to use relative coordinates (i.e. are placed at 0,0) and thus use the BSPs linked object's position and rotation for placement.
  (PrefabPlacements also have pos/rot info, but it makes no sense yet.)
- A few BSPs don't have a linked object, and they are also placed at (0,0), and for them it's not yet clear where to place them.

Examples on the first map in FEAR2 (m01_penthouse) include:
- The tower front glowing illumination (WM 107, type 0 instance ref to Face 140 and 11 more)
- The Valkyrie tower sign text "glow" (WM 108, type 1 prefab ref to "prefabs\e01\signs\valkyrietower.inst")
- A part of a curved walk in the corner of the map (WMs 892, 893, 894; type 0 instance refs to Faces 864+2, 883+1, 867 respectively)

Additional findings on that topic:
- These BSPs have no name and therefore no linked object. Usually they refer to faces in the global mesh (via BSP render instances list), also placed at (0,0).
- There don't seem to be remaining objects that could give position to these objects, nor do the BSPs or faces before/after them in the order give any clues.
- There does not seem to be anything in the objects to link them to those BSPs. There is a "BlindObjectIndex" that has not been resolved yet, but it does not look like it applies to those types of objects.
- I have checked manually if ANY objects/worldmodels would provide the correct position if the models were placed there, but none did. You can verify that e.g. for the Valkyrie tower sign (see above), which is centered in its mesh, there is NO "object" on the map at those coordinates.
- It would be possible that coordinates are assigned dynamically, but any command (message) in other objects would need to reference the targets via an index, because they have no names, and such commands have not been found.
- It seem unlikely that any other entities could provide position, like sectors or the streaming data section at the end of the .wld file.
- Also most structures have been decoded, and even if there were others, it's not clear how they could link to the BSPs, as the BSP has no more fields that could provide a link themselves to other entities.

If you have any idea about the solution to this mystery, feel free to open an "issue" about it. :-)