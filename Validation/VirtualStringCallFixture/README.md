# Virtual string call and class initialization control

This synthetic fixture isolates a virtual, zero-argument string getter. Its
inherited nonvirtual helper reads a private field on its own class and casts
the value to a derived class. The field's source type is in the embedded
`Neutral.CastHierarchy` package; the cast target, helper, and getter are in
`VirtualStringCallFixture`. The getter reads an inherited string field and
tail-calls the static two-string `System.String.Concat` with a literal. The
null native argument is hidden method metadata.

Two public classes in the embedded hierarchy have explicit static
constructors: one lies between the source and target types, and one lies
above the source type. A witness records their order. The validation driver
first constructs the target while both initializers are cold, then repeats
the construction to check that neither initializer runs again. It also
checks null and nonnull failed casts, successful casts, unchanged neighboring
fields, and virtual override dispatch. The override and a further-derived
node belong to the validation harness, outside the five-method selected
recovery assembly.

Earlier isolated baselines under ignored `Files/` retain an instance-call
negative control, a public inherited-field cast control, and an exact
cross-assembly variant with the intermediate static constructor in the
selected assembly. The preceding split checked failed casts before class
initialization; the current split checks the first and repeated target
construction. Both keep the intermediate initializer in the package to
isolate the remaining selected constructor recovery gap. These controls
do not establish recovered behavior. The package source and compiled
assembly are explicit synthetic dependencies, recorded separately from the
selected player input. An original Windows x64 Release IL2CPP build
establishes the native shape before a recovery proof can be claimed.
