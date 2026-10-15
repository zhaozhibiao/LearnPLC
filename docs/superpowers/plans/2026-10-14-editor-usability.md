# Engineering resource menus and instruction library

User-authorized design: resource right-click menus target the clicked resource, with add/open/duplicate/rename/delete/properties. Category menus add the correct kind. Task and hardware properties open their existing configuration editors; task menus maintain the single enabled cyclic task constraint. Delete remains confirmed and refuses task-bound programs.

Right application sidebar: searchable categorized own ST templates, description and insertion status; left-button drag to the actual editor position, double-click or button insertion at caret. Common boolean/gating, flow/segments, arithmetic/comparison/conversion, edge/latch, timer/counter, and motion interface reference templates. Stateful templates add unique declarations within the target POU, using one undo transaction. Comments and strings do not determine insertion boundaries. Motion reference entries explicitly remain commented until the bridge is implemented. No new libraries or device writes.

Steps: (1) core catalog and lexical insertion tests, failing then implemented; (2) right sidebar and drag routing, editor undo integration; (3) context menus and target-correct properties/duplicate/task handling; (4) headless pointer/menu/insertion/undo/search tests and real compiler checks for standard templates; (5) screenshots, review, docs. All work remains in D:/OPenplc and preserves unrelated changes.

References: https://edge.autonomylogic.com/docs/openplc-editor/workspace-overview/project-explorer/ ; https://help.plc.abb.com/overview_operators_and_library_elements.html . Reference behavior only; no vendor code or assets copied.
