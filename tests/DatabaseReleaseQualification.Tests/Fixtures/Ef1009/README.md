These non-sensitive fixtures reproduce the exact bytes emitted by dotnet-ef
10.0.9 / SQL Server provider 10.0.9 from Luana's original Infrastructure and
Api projects, with Cqrs pinned to 10.11.8 (Luana commit 536f30931053c7a0f5bb23792ebc89a38612e45c).
The scripts cover 20260630141200_AddTablaPrueba2 to
20260701161500_AddSeblobTable and the reverse transition.

They were reconstructed from the recorded offline output and verified against
the original SHA256 values, retaining UTF-8 BOM, CRLF and final blank line:

- forward.sql: 309581D85A1525F03F0125337EC39655F89E8E6A2E4A02D48C415A096024B47F
- rollback.sql: C05451E73BA4ECE0E69A21E6B4D8AA9DFF227009CD37B1ED7128EA452808F1F9

Tests only parse/validate these files. They never execute SQL or connect to a database.
The scoped Git binary attribute preserves exact bytes on Windows and Linux.
