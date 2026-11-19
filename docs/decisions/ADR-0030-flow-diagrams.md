# ADR-0030: Flow diagrams (a `Diagram` block of nodes and connectors)

- **Status:** Accepted (2026-11-18, product owner: "Flussdiagramme", details chosen by the implementer: recommended defaults); implemented in .NET
- **Date:** 2026-11-18
- **Context:** "Connectors and flow diagrams" row of "Out of scope" in the feature matrix. The drawing layer
  (ADR-0022) has lines, rectangles, ellipses, freeforms and text boxes, but no arrowheads, no connection between
  shapes and no flow-chart symbols. Building a flow chart from `DrawingShape`s means working out every coordinate and
  every arrowhead by hand, and several floating shapes in separate paragraphs do not keep their positions to each other.

## Decision

1. **One block.** `Diagram` is a block like `Chart`: a fixed `Width` and `Height` and, inside it, `Nodes`
   (`FlowNode`) and `Connectors` (`FlowConnector`). It stands in the text flow like an inline image and reserves its
   size. Not in headers, footers, notes (reported and omitted, as for charts).
2. **Nodes** have a unique string `Id`, a `Kind` (`Process` = rectangle, `Decision` = diamond, `Terminator` = pill,
   `InputOutput` = parallelogram), a `Text`, a position and a size inside the diagram, optional `Fill`, `Outline`
   and `TextColor` (light fill, dark outline and black text by default) and a `FontSize` (10 pt by default). The text
   is centred; a long text wraps inside the node.
3. **Connectors** run from a side (`Top`, `Right`, `Bottom`, `Left`) of one node to a side of another, with a
   `Route` (`Straight`: one line between the two points; `Elbow`: horizontal and vertical segments, default),
   optional `Label` ("Yes"/"No" on a decision), `Color`, `Dash`, `ArrowEnd` (default true) and `ArrowStart`
   (default false). The library works out the path from the node geometry; **the caller places the nodes** and
   there is no automatic layout. The elbow route leaves each side by a short stub and joins the stubs with one or two
   bends; it does **not** avoid other nodes. A connector to an unknown node id is rejected at construction.
4. **Plain drawing, no glue.** A connector is drawn as an open path plus a small filled triangle for each arrowhead.
   It is not anchored to the node: moving a shape in Word or LibreOffice does not move the connector. Real connection
   sites (`a:stCxn`, `draw:connector`) are not written because Word cannot be driven automatically and a wrong
   anchor makes a connector jump.
5. **Expansion to existing primitives.** `Diagram` is turned into a list of `DrawingShape`s with positions
   (nodes: a text box for a process, a freeform plus a text box on top for the other kinds; connectors: freeforms;
   labels: text boxes without fill or outline). The three writers place that list; there is no new shape kind and no
   new geometry in the writers.
6. **Formats.**
   - **DOCX:** one inline group `wpg:wgp` in `wp:inline` with a `wps:wsp` per shape (the single-shape writer is reused;
     only the offset in the group differs). Needs Word 2010 or later.
   - **ODT:** one `draw:g` anchored as character, with the shapes as children at `svg:x`/`svg:y`.
   - **PDF:** the shapes are drawn at their positions inside a reserved area of the diagram's size, text boxes laid
     out as before.
7. **Reading.** Not in version 1. A group written by the library is reported like any group
   ("a shape group or canvas is not mapped"); an untouched document keeps it through the preserving round trip.
   Reading groups back into a `Diagram` is possible later because the shape order and kinds are fixed.
8. **Limits.** At most 1,000 nodes and 4,000 connectors per diagram, sizes finite and positive, nodes inside the
   diagram area (a node outside is rejected at construction).
9. **Swift port.** Same model; the expansion produces the same shapes, byte-identical packages in the cross-parity job.

## Consequences

- A flow chart is a few lines of model code and looks the same in DOCX, ODT and PDF.
- The elbow route can run through a node when the nodes are placed unfavourably; the caller adjusts placement or
  chooses the side. This is documented and tested with the common cases only (straight down, side branch, loop back).
- A Diagram is one object, so editing one box in Word means editing inside the group.
- Estimating the wrapped height of a node text without a font is approximate; a long text in a small node can overflow
  (clipped by the text box).

## Findings from the implementation (2026-11-18)

- ODT writes a diagram in headers, footers and notes as well (no extra code); DOCX reports it there. PDF draws it in
  the body and (since 2026-11-19) in table cells, scaled to the cell width.
- LibreOffice ignores the top margin of the first paragraph in a text box, so in ODT the text of a node sat at the
  top. Since 2026-11-19 the text boxes of a diagram get their own graphic style with `draw:textarea-vertical-align="middle"`.
- Reading: the DOCX group is reported ("a drawing without a media part is not mapped"); in ODT a `draw:g` is reported
  and its children are no longer read as loose shapes.
- PDF scales positions and sizes down to fit the page, but not the font size of the nodes.

## Not decided here

Automatic layout; swimlanes; connectors that avoid obstacles; real glued connectors; a node with its own picture;
curved connectors; reading diagrams back.

## Word check (2026-11-19)

- A diagram shrunk into a table cell cut off its node text in Word: the DOCX group scaled the shapes (`chExt`) but not
  the text, and the space that centres the text, computed for the full height, pushed it out of the boxes. Now
  `DiagramLayout.Scale` scales the laid-out shapes themselves (geometry, paths, outline widths, font sizes, paragraph
  spacing) and DOCX and PDF draw those; the group is not scaled any more. ODT keeps the full size.
- Second Word check: node text fits; the connector labels ("Ja", "Nein") still vanished, because the text margins
  stayed at Word's defaults (7.2 pt left and right) while the label boxes shrank. The DOCX writer now writes the
  margins of a shrunk diagram scaled (`lIns`, `tIns`, `rIns`, `bIns`), the PDF scales its 4 pt padding the same way.
- ODT (2026-11-19): a diagram wider than its place (text area or table cell) is shrunk with its text like in DOCX and
  PDF; `OdtDiagrams` works out the place of every diagram once per write so that the style collectors and the content
  writer see the same scaled runs.
