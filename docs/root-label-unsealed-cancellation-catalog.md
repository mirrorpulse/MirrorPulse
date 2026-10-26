# Cancellation evidence before a namespace move is sealed

Schema92 adds a separate immutable cancellation owner for a public root Label
preparation that never acquired a namespace move. It retains the original Label
ID/public proof digest, complete guarded current tree, member identities,
positions and protection epochs, plus the final actual public `NotMoved` facts
supplied by the Host. The record creates no native move admission, reservation,
predecessor claim, permission authority, current readiness or journal ACK.

The final SQLite transaction requires the exact original pending Label and an
unchanged configured source. It refuses any move/progress/reservation/predecessor
or sealed-cancellation row belonging to that ID, as well as other unresolved
namespace owners for the selected root. Complete current membership, original
member/identity/position provenance, verified protection and final native facts
must agree. Recording the new cancellation and closing only its original Label
share one transaction; an injected final Label-write failure rolls both back.

The root-leading index and table are mandatory in schema92. Lost/replaced
definitions refuse repair; an older schema cannot adopt nonempty cancellation
ownership or a later marker. A distinct Label marker prevents a missing record
from being read as harmless legacy cancellation. Historical replay validates
retained evidence and cannot alter a later Label or its real move claim.

Core preserves CfSharp evidence as opaque bytes. These catalog tests use
synthetic metadata and real SQLite transactions; they establish neither actual
SDK `NotMoved` nor native guards. The Host bridge retains complete physical
anchor/tree guards through reconciliation and commit, then separately recovers
and audits its current role. Its native producer includes both enabled/disabled
preparations across independent role disposal. Managed catalog results alone
do not accept that older-role Host/runtime path or complete S06; clean native
and independently restarted signed Host evidence remain required.
