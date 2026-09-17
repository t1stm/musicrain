# ReSharper inspection findings

Generated with `jb inspectcode Backend/Gaida.slnx --severity=SUGGESTION`.

25 open findings.

## WARNING (21 issues, 13 types)

### '??' condition is never null according to nullable reference types' annotations (`NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract`) — 4

<https://www.jetbrains.com/help/resharper/NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract.html>

- `Services\Dom\Controllers\Playlists.cs:105` — '??' left operand is never 'null' according to nullable reference types' annotations
- `Services\Dom\Store\DomStore.cs:98` — '??' left operand is never 'null' according to nullable reference types' annotations
- `Services\Dom\Store\DomStore.cs:102` — '??' left operand is never 'null' according to nullable reference types' annotations
- `Services\Dom\Store\DomStore.cs:487` — '??' left operand is never 'null' according to nullable reference types' annotations

### Conditional access qualifier expression is not null according to nullable reference types' annotations (`ConditionalAccessQualifierIsNonNullableAccordingToAPIContract`) — 3

- `Services\Gaida.Bot\Commands\PlaybackCommands.cs:251` — Conditional access qualifier expression is never null according to nullable reference types' annotations
- `Services\Gaida.Bot\Commands\PlaybackCommands.cs:520` — Conditional access qualifier expression is never null according to nullable reference types' annotations
- `Services\Gaida.Bot\Program.cs:129` — Conditional access qualifier expression is never null according to nullable reference types' annotations

### Parameter is only used for precondition check: Private accessibility (`ParameterOnlyUsedForPreconditionCheck.Local`) — 2

<https://www.jetbrains.com/help/resharper/ParameterOnlyUsedForPreconditionCheck.Local.html>

- `Tests\Gaida.Tests\MultiplayerTests.cs:194` — Parameter 'queue' is only used for precondition check(s)
- `Tests\Gaida.Tests\MultiplayerTests.cs:820` — Parameter 'sendFails' is only used for precondition check(s)

### Assignment is not used (`RedundantAssignment`) — 2

<https://www.jetbrains.com/help/resharper/RedundantAssignment.html>

- `Services\Stih\LyricsIndex.cs:52` — The value passed to the method is never used because it is overwritten in the method body before being read
- `Services\Stih\LyricsIndex.cs:52` — Value assigned is not used in any execution path

### Unused parameter: Private accessibility (`UnusedParameter.Local`) — 2

<https://www.jetbrains.com/help/resharper/UnusedParameter.Local.html>

- `Platforms\Gaida.Pods.MusicDatabase\Program.cs:204` — Parameter 'url' is never used
- `Services\Gaida.Bot\Players\Player.cs:217` — Parameter 'fed' is never used

### Potentially misleading parameter name in lambda or local function (`AllUnderscoreLocalParameterName`) — 1

<https://www.jetbrains.com/help/resharper/AllUnderscoreLocalParameterName.html>

- `Services\Stih\LyricsIndex.cs:52` — The '_' name is typically reserved for parameters without usages

### Heuristically unreachable code (`HeuristicUnreachableCode`) — 1

<https://www.jetbrains.com/help/resharper/HeuristicUnreachableCode.html>

- `Tests\Gaida.Tests\StreamingTests.cs:155` — Code is heuristically unreachable

### Inconsistent synchronization on field (`InconsistentlySynchronizedField`) — 1

<https://www.jetbrains.com/help/resharper/InconsistentlySynchronizedField.html>

- `Services\Gaida.Bot\Players\Playlist.cs:18` — The field is sometimes used inside synchronized block and sometimes used without synchronization

### Member hides static member from outer class (`MemberHidesStaticFromOuterClass`) — 1

<https://www.jetbrains.com/help/resharper/MemberHidesStaticFromOuterClass.html>

- `Services\Stih\SelfCheck.cs:221` — Property 'List<string> Paths' hides method from outer class

### Private field can be converted into local variable (`PrivateFieldCanBeConvertedToLocalVariable`) — 1

<https://www.jetbrains.com/help/resharper/PrivateFieldCanBeConvertedToLocalVariable.html>

- `Services\Stih\LyricsIndex.cs:36` — The field is always assigned before being used and can be converted into a local variable

### Do not use object initializer for 'using' variable: Do not use object initializer for 'using' variable (`UsingStatementResourceInitialization`) — 1

<https://www.jetbrains.com/help/resharper/UsingStatementResourceInitialization.html>

- `Services\Dunav\SelfCheck.cs:57` — Initialize object properties inside the 'using' statement to ensure that the object is disposed if an exception is thrown during initialization

### Variable in local function hides variable from outer scope (`VariableHidesOuterVariable`) — 1

<https://www.jetbrains.com/help/resharper/VariableHidesOuterVariable.html>

- `Services\Gaida.Bot\Commands\PlaybackCommands.cs:393` — Parameter 'index' hides outer local variable with the same name

### RoslynAnalyzers Do not use Where clause with Assert.Single (`xUnit2031`) — 1

- `Tests\Gaida.Tests\BotPlaybackTests.cs:356` — Do not use a Where clause to filter before calling Assert.Single. Use the overload of Assert.Single that accepts a filtering function.

## SUGGESTION (4 issues, 1 types)

### Method supports cancellation (`MethodSupportsCancellation`) — 4

<https://www.jetbrains.com/help/resharper/MethodSupportsCancellation.html>

- `Tests\Gaida.Tests\StreamSpreaderTests.cs:170` — Method has overload with cancellation support
- `Tests\Gaida.Tests\StreamSpreaderTests.cs:176` — Method has overload with cancellation support
- `Tests\Gaida.Tests\StreamSpreaderTests.cs:180` — Method has overload with cancellation support
- `Tests\Gaida.Tests\StreamSpreaderTests.cs:184` — Method has overload with cancellation support
