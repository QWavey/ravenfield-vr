// Publicizer: rewrites a managed assembly so every type, method and field is public.
// The output is used only as a compile-time reference for the mod; the game keeps
// loading its original assembly. Mono does not enforce member visibility at JIT time,
// so code compiled against the publicized copy runs against the real one.
//
// Usage: Publicizer.exe <input.dll> <output.dll> [searchDir ...]
using System;
using System.IO;
using System.Linq;
using Mono.Cecil;

internal static class Publicizer
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: Publicizer <input.dll> <output.dll> [searchDir ...]");
            return 2;
        }

        string input = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(input));
        foreach (string dir in args.Skip(2))
        {
            resolver.AddSearchDirectory(dir);
        }

        var readerParams = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate };
        using (AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(input, readerParams))
        {
            int types = 0, methods = 0, fields = 0;
            foreach (TypeDefinition type in asm.MainModule.GetTypes())
            {
                if (type.IsNested)
                {
                    type.IsNestedPublic = true;
                }
                else
                {
                    type.IsPublic = true;
                }
                types++;

                foreach (MethodDefinition method in type.Methods)
                {
                    if (!method.IsPublic)
                    {
                        method.IsPublic = true;
                        methods++;
                    }
                }

                foreach (FieldDefinition field in type.Fields)
                {
                    // Events are backed by a private field of the same name; making it public would
                    // collide with the event in C# name lookup, so leave those alone.
                    if (type.Events.Any(e => e.Name == field.Name))
                    {
                        continue;
                    }
                    if (!field.IsPublic)
                    {
                        field.IsPublic = true;
                        fields++;
                    }
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(output));
            asm.Write(output);
            Console.WriteLine($"publicized {Path.GetFileName(input)}: {types} types, {methods} methods, {fields} fields -> {output}");
        }
        return 0;
    }
}
