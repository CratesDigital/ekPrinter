# Contributing

Issues and pull requests are welcome.

- **Bug reports:** say which Windows version, which printer and driver, and
  what the agent page's *Recent jobs* showed. For printing problems, a photo
  of the printout next to the screen helps more than a description.
- **Security issues:** do not open a public issue; see [SECURITY.md](SECURITY.md).
- **Pull requests:** keep them focused, and build with `dotnet publish -c Release`
  on Windows before opening one — CI builds the installer on every push.
  New source files carry the Apache-2.0 header used by the existing ones.
- **Printer quirks** are the most useful contribution of all: if a driver
  needs something special to cut, feed or open its drawer, document it in
  `docs/design.md` with the model and driver version.

By contributing you agree your contribution is licensed under Apache-2.0, the
licence of this project.
