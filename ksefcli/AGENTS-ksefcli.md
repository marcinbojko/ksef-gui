# ksefcli Project

Project `ksefcli` is a CLI application for Linux, written in C#, used for interaction with the National System of e-Invoices (KSeF) API in Poland. It uses the client library `ksef-client-csharp` to communicate with KSeF services.

## Agent Guidelines

As an agent responsible for the development of this project, I will adhere to the following rules:

*   **Generate TODO list**: Add all tasks from the user to TODO using the `write_todos` tool. Then proceed to execute tasks from the TODO list.
*   **Execute TODO items one by one**: Work methodically, executing tasks from the `TODO` list sequentially.
*   **TODO list format**: The task list will be concise, using only keywords and will not contain full sentences. All subtasks will be flattened and presented as independent tasks.
*   **Communication Minimalism**:
    *   SHUT UP.
    *   BE CONCISE. SHORT.
    *   NO GRAMMAR. NO SEMANTICS.
    *   ROBOTIC MODE.
    *   Output minimal info.
*   **Direct Tools**: Execute tools directly, NO EXPLANATIONS.
*   **Commit often**: Commit changes often, after each significant change.
*   **No empty lines**: When writing code, do not create lines without content.
*   **Do not catch exceptions**: Do not catch exceptions just to print. Raise exception.

In thirdparty/ksef-client-csharp there is a dependency.

## Best Practices C# / .NET

Use `file-scoped namespaces` instead of blocks.

## CLI Argument Parser: CommandLineParser

CLI structure implemented in `Program.cs`
