# Declaration-only assembly fixture

This separate synthetic assembly contains an enum, a generic interface and a
struct with fields. Its interface method requires no managed implementation;
the other types declare no methods. Strict source recovery must preserve the
assembly without inventing executable bodies or counting these declarations as
recovered behavior.

Stage the fixture beside an executable fixture in a fresh ignored source tree,
build with the exact target, then select only `DeclarationOnlyFixture` for
player-only source recovery. Compare its stripped managed declarations, verify
the recovered managed assembly and compile the generated source in the exact
editor. The executable driver's behavior checks are outside this assembly's
scope. Retain missing native bodies as failures when a managed implementation
actually is required.
