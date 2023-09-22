using Microsoft.VisualBasic;
using Mono.Cecil;
using Mono.Cecil.Rocks;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text.RegularExpressions;

namespace CheckObfuscation
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: CheckObfuscation.exe <file|directory>");
                return -1;
            }

            try
            {
                string path = args[0];
                var checker = new ObfuscationChecker();
                int count = checker.Process(path); 
                return count;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
            }
            return -1;
        }
    }

    class ObfuscationChecker
    {
        private const string ModuleTypeName = "<Module>";
        private int MaxNameLength = 3;

        public double RenamingPercentage { get; set; }
        public bool HasBabelObfuscatorAttribute { get; set; }
        public bool HasModuleInitializer { get; set; }

        public ObfuscationChecker()
        {
        }

        public int Process(string path)
        {
            if (Directory.Exists(path))
            {
                // If the path is a directory, scan all .DLL and .EXE files
                var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                    .Where(f => Regex.IsMatch(f, @"\.dll$|\.exe$", RegexOptions.IgnoreCase));

                return files.Count(file => CheckIfObfuscated(file));
            }
 
            return CheckIfObfuscated(path) ? 1 : 0;
        }

        public bool CheckIfObfuscated(string path)
        {
            try
            {
                var asm = AssemblyDefinition.ReadAssembly(path);
                if (IsObfuscated(asm))
                {
                    Console.WriteLine($"{asm.Name.Name} obfuscated");
                    Console.WriteLine($"   Estimated renaming percentage: {RenamingPercentage:P}");
                    Console.WriteLine($"   Module Initializer: {HasModuleInitializer}");
                    Console.WriteLine($"   BabelObfuscator attribute: {HasBabelObfuscatorAttribute}");
                    return true;
                }

                if (RenamingPercentage > 0.2)
                    Console.WriteLine($"{asm.Name.Name} lightly obfuscated");
                else
                    Console.WriteLine($"{asm.Name.Name} not obfuscated");
            }
            catch (BadImageFormatException)
            {
                // Ignore the BadImageFormatException of invalid exe or libraries
            }

            return false;
        }

        public bool IsObfuscated(AssemblyDefinition assembly)
        {
            // Check if BabelObfuscatorAttribute is present at assembly level
            HasBabelObfuscatorAttribute = assembly.CustomAttributes.Any(a => a.AttributeType.Name == "BabelObfuscatorAttribute");
            
            // Calculate renaming percentage
            RenamingPercentage = GetRenamingPercentage(assembly);

            // Module initializer is a common pattern in babel generated code
            // for decryption and other initialization tasks.
            HasModuleInitializer = HasModuleInitializerMethod(assembly);

            // Check if the assembly is obfuscated
            return (RenamingPercentage > 0.4) || HasBabelObfuscatorAttribute || HasModuleInitializer;
        }

        public double GetRenamingPercentage(AssemblyDefinition assembly)
        {
            double obfuscatedMember = 0;
            double totalMembers = 0;

            foreach (var member in AllMembersDefined(assembly))
            {
                totalMembers++;

                if (IsLikelyObfuscated(member))
                {
                    obfuscatedMember++;
                }
            }

            if (totalMembers > 1)
            {
                return obfuscatedMember / totalMembers;
            }

            return 0.0;
        }

        public bool HasModuleInitializerMethod(AssemblyDefinition assembly)
        {
            var moduleType = assembly.MainModule.GetType(ModuleTypeName);
            if (moduleType == null)
                return false;

            // Search module initializer target
            var moduleInitializer = moduleType.Methods.FirstOrDefault(m => m.Name == "@!");
            if (moduleInitializer != null)
                return true;

            return false;
        }

        private IEnumerable<IMemberDefinition> AllMembersDefined(AssemblyDefinition assembly)
        {
            foreach (var type in assembly.Modules.SelectMany(module => module.GetAllTypes()))
            {
                yield return type;

                foreach (var field in type.Fields.Where(m => !m.IsSpecialName && !m.IsRuntimeSpecialName))
                    yield return field;

                foreach (var property in type.Properties)   
                    yield return property;

                foreach (var evt in type.Events)
                    yield return evt;

                foreach (var method in type.Methods.Where(m => !m.IsSpecialName && !m.IsRuntimeSpecialName))
                    yield return method;
            }
        }

        private bool IsLikelyObfuscated(IMemberDefinition member)
        {
            // Check if unicode name
            if (!IsAscii(member.Name))
                return true;

            // Unicode normalization is disabled

            // We can have a mix of ASCII lowercase characters to compose a short symbol names.
            // This is a common pattern in babel generated code.
            if (member.Name.Length <= MaxNameLength)
                return IsLowercase(member.Name);

            // Or similar names for members that are tied to the same parent
            string root = member.Name.Substring(0, member.Name.Length - MaxNameLength);
            if (IsLowercase(root))
            {
                var similar = GetRelatedMembers(member).ToList();
                if (similar.Count > 0)
                {
                    double sameNameFactor = similar.Count(t => t.Name.StartsWith(root)) / (1.0 * similar.Count);
                    return sameNameFactor > 0.5;
                }
            }

            // If you use non standard naming schema add your logic here

            return false;
        }

        private IEnumerable<IMemberDefinition> GetRelatedMembers(IMemberDefinition member)
        {
            if (member is TypeDefinition type)
                return type.Module.GetAllTypes().Where(t => t.Namespace == type.Namespace);
            else if (member.DeclaringType != null)
            {
                if (member is FieldDefinition)
                    return member.DeclaringType.Fields;
                else if (member is PropertyDefinition)
                    return member.DeclaringType.Properties;
                else if (member is MethodDefinition)
                    return member.DeclaringType.Methods.Where(m => !m.IsSpecialName && !m.IsRuntimeSpecialName);
                else if (member is EventDefinition)
                    return member.DeclaringType.Events;
            }
            return Enumerable.Empty<IMemberDefinition>();   
        }

        private static bool IsLowercase(string name)
        {
            return name.All(c => char.IsLower(c));
        }

        private static bool IsAscii(string text)
        {
            return text.All(c => ((int)c) <= 127);
        }
    }
}
