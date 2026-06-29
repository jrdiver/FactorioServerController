## Code Style Rules
- Avoid using ar unless the type is anonymous. Always specify explicit types for variables.
- Do not use brackets {} for single-line if, or, or oreach statements.
- Use C# 12 collection expressions (e.g., []) instead of 
ew List<T>() or 
ew T[] whenever possible.
- Use target-typed 
ew() expressions when the type is apparent (e.g., 
ew(args) instead of 
ew ClassName(args)).
- Prefer keeping expressions on a single line. Do not break long lines (e.g. constructor parameters, chained LINQ queries) unless they get ridiculously long.
