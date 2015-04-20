﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Linq;

using Microsoft.Build.Shared;
using System.Text;
using System.Runtime.Versioning;

namespace Microsoft.Build.Tasks
{
    /// <summary>
    /// Collection of methods used to discover assembly metadata.
    /// Primarily stolen from manifestutility.cs AssemblyMetaDataImport class.
    /// </summary>
    internal class AssemblyInformation : DisposableBase
    {
        private AssemblyNameExtension[] _assemblyDependencies = null;
        private string[] _assemblyFiles = null;
        private IMetaDataDispenser _metadataDispenser = null;
        private IMetaDataAssemblyImport _assemblyImport = null;
        private static Guid s_importerGuid = new Guid(((GuidAttribute)Attribute.GetCustomAttribute(typeof(IMetaDataImport), typeof(GuidAttribute), false)).Value);
        private string _sourceFile;
        private FrameworkName _frameworkName;
        private static string s_targetFrameworkAttribute = "System.Runtime.Versioning.TargetFrameworkAttribute";
        private readonly Assembly _assembly;

        // Borrowed from genman.
        private const int GENMAN_STRING_BUF_SIZE = 1024;
        private const int GENMAN_LOCALE_BUF_SIZE = 64;
        private const int GENMAN_ENUM_TOKEN_BUF_SIZE = 16; // 128 from genman seems too big.

        static AssemblyInformation()
        {
            AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += ReflectionOnlyAssemblyResolve;
        }

        /// <summary>
        /// Construct an instance for a source file.
        /// </summary>
        /// <param name="sourceFile">The assembly.</param>
        internal AssemblyInformation(string sourceFile)
        {
            // Extra checks for PInvoke-destined data.
            ErrorUtilities.VerifyThrowArgumentNull(sourceFile, "sourceFile");
            _sourceFile = sourceFile;

            if (NativeMethodsShared.IsWindows)
            {
                // Create the metadata dispenser and open scope on the source file.
                _metadataDispenser = (IMetaDataDispenser)new CorMetaDataDispenser();
                _assemblyImport = (IMetaDataAssemblyImport)_metadataDispenser.OpenScope(sourceFile, 0, ref s_importerGuid);
            }
            else
            {
                _assembly = Assembly.ReflectionOnlyLoadFrom(sourceFile);
            }
        }

        private static Assembly ReflectionOnlyAssemblyResolve(object sender, ResolveEventArgs args)
        {
            string[] nameParts = args.Name.Split(',');
            Assembly assembly = null;

            if (args.RequestingAssembly != null)
            {
                var location = args.RequestingAssembly.Location;
                var newLocation = Path.Combine(Path.GetDirectoryName(location), nameParts[0].Trim() + ".dll");

                try
                {
                    if (File.Exists(newLocation))
                    {
                        assembly = Assembly.ReflectionOnlyLoadFrom(newLocation);
                    }
                }
                catch
                {
                }
            }

            // Let's try to automatically load it
            if (assembly == null)
            {
                try
                {
                    assembly = Assembly.ReflectionOnlyLoad(args.Name);
                }
                catch
                {
                }
            }

            return assembly;
        }

        /// <summary>
        /// Get the dependencies.
        /// </summary>
        /// <value></value>
        public AssemblyNameExtension[] Dependencies
        {
            get
            {
                if (_assemblyDependencies == null)
                {
                    lock (this)
                    {
                        if (_assemblyDependencies == null)
                        {
                            _assemblyDependencies = ImportAssemblyDependencies();
                        }
                    }
                }

                return _assemblyDependencies;
            }
        }

        /// <summary>
        /// Get the scatter files from the assembly metadata. 
        /// </summary>
        /// <value></value>
        public string[] Files
        {
            get
            {
                if (_assemblyFiles == null)
                {
                    lock (this)
                    {
                        if (_assemblyFiles == null)
                        {
                            _assemblyFiles = ImportFiles();
                        }
                    }
                }

                return _assemblyFiles;
            }
        }

        /// <summary>
        /// What was the framework name that the assembly was built against.
        /// </summary>
        public FrameworkName FrameworkNameAttribute
        {
            get
            {
                if (_frameworkName == null)
                {
                    lock (this)
                    {
                        if (_frameworkName == null)
                        {
                            _frameworkName = GetFrameworkName();
                        }
                    }
                }

                return _frameworkName;
            }
        }

        /// <summary>
        /// Given an assembly name, crack it open and retrieve the list of dependent 
        /// assemblies and  the list of scatter files.
        /// </summary>
        /// <param name="path">Path to the assembly.</param>
        /// <param name="dependencies">Receives the list of dependencies.</param>
        /// <param name="scatterFiles">Receives the list of associated scatter files.</param>
        /// <param name="frameworkName">Gets the assembly name.</param>
        internal static void GetAssemblyMetadata
        (
            string path,
            out AssemblyNameExtension[] dependencies,
            out string[] scatterFiles,
            out FrameworkName frameworkName
        )
        {
            AssemblyInformation import = null;
            using (import = new AssemblyInformation(path))
            {
                dependencies = import.Dependencies;
                frameworkName = import.FrameworkNameAttribute;
                scatterFiles = NativeMethodsShared.IsWindows ? import.Files : null;
            }
        }

        /// <summary>
        /// Given an assembly name, crack it open and retrieve the TargetFrameworkAttribute
        /// assemblies and  the list of scatter files.
        /// </summary>
        internal static FrameworkName GetTargetFrameworkAttribute(string path)
        {
            using (AssemblyInformation import = new AssemblyInformation(path))
            {
                return import.FrameworkNameAttribute;
            }
        }

        /// <summary>
        /// Determine if an file is a winmd file or not.
        /// </summary>
        internal static bool IsWinMDFile(
            string fullPath,
            GetAssemblyRuntimeVersion getAssemblyRuntimeVersion,
            FileExists fileExists,
            out string imageRuntimeVersion,
            out bool isManagedWinmd)
        {
            imageRuntimeVersion = String.Empty;
            isManagedWinmd = false;

            if (!NativeMethodsShared.IsWindows)
            {
                return false;
            }

            // May be null or empty is the file was never resolved to a path on disk.
            if (!String.IsNullOrEmpty(fullPath) && fileExists(fullPath))
            {
                imageRuntimeVersion = getAssemblyRuntimeVersion(fullPath);
                if (!String.IsNullOrEmpty(imageRuntimeVersion))
                {
                    bool containsWindowsRuntime = imageRuntimeVersion.IndexOf(
                        "WindowsRuntime",
                        StringComparison.OrdinalIgnoreCase) >= 0;

                    if (containsWindowsRuntime)
                    {
                        isManagedWinmd = imageRuntimeVersion.IndexOf("CLR", StringComparison.OrdinalIgnoreCase) >= 0;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Get the framework name from the assembly.
        /// </summary>
        private FrameworkName GetFrameworkName()
        {
            if (!NativeMethodsShared.IsWindows)
            {
                CustomAttributeData attr = null;

                foreach (CustomAttributeData a in _assembly.GetCustomAttributesData())
                {
                    try
                    {
                        if (a.AttributeType.Equals(typeof(TargetFrameworkAttribute)))
                        {
                            attr = a;
                            break;
                        }
                    }
                    catch
                    {
                    }
                }

                object name = null;
                if (attr != null)
                {
                    name =
                        attr.NamedArguments.FirstOrDefault(
                            t =>
                            t.MemberName.Equals("FrameworkDisplayName", StringComparison.InvariantCultureIgnoreCase));
                }
                return name == null ? null : name as FrameworkName;
            }

            FrameworkName frameworkAttribute = null;
            try
            {
                IMetaDataImport2 import2 = (IMetaDataImport2)_assemblyImport;
                IntPtr data = IntPtr.Zero;
                UInt32 valueLen = 0;
                string frameworkNameAttribute = null;
                UInt32 assemblyScope;

                _assemblyImport.GetAssemblyFromScope(out assemblyScope);
                int hr = import2.GetCustomAttributeByName(assemblyScope, s_targetFrameworkAttribute, out data, out valueLen);

                // get the AssemblyTitle
                if (hr == NativeMethodsShared.S_OK)
                {
                    // if an AssemblyTitle exists, parse the contents of the blob
                    if (NativeMethods.TryReadMetadataString(_sourceFile, data, valueLen, out frameworkNameAttribute))
                    {
                        if (!String.IsNullOrEmpty(frameworkNameAttribute))
                        {
                            frameworkAttribute = new FrameworkName(frameworkNameAttribute);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (ExceptionHandling.IsCriticalException(e))
                {
                    throw;
                }
            }

            return frameworkAttribute;
        }

        /// <summary>
        /// Release interface pointers on Dispose(). 
        /// </summary>
        protected override void DisposeUnmanagedResources()
        {
            if (NativeMethodsShared.IsWindows)
            {
                if (_assemblyImport != null)
                    Marshal.ReleaseComObject(_assemblyImport);
                if (_metadataDispenser != null)
                    Marshal.ReleaseComObject(_metadataDispenser);
            }
        }

        /// <summary>
        /// Given a path get the CLR runtime version of the file
        /// </summary>
        /// <param name="path">path to the file</param>
        /// <returns>The CLR runtime version or empty if the path does not exist.</returns>
        internal static string GetRuntimeVersion(string path)
        {
            if (NativeMethodsShared.IsWindows)
            {
                StringBuilder runtimeVersion = null;
                uint hresult = 0;
                uint actualBufferSize = 0;
#if _DEBUG
                // Just to make sure and exercise the code that doubles the size
                // every time GetRequestedRuntimeInfo fails due to insufficient buffer size.
                int bufferLength = 1;
#else
                int bufferLength = 11; // 11 is the length of a runtime version and null terminator v2.0.50727/0
#endif
                do
                {
                    runtimeVersion = new StringBuilder(bufferLength);
                    hresult = NativeMethods.GetFileVersion(path, runtimeVersion, bufferLength, out actualBufferSize);
                    bufferLength = bufferLength * 2;
                } while (hresult == NativeMethodsShared.ERROR_INSUFFICIENT_BUFFER);

                if (hresult == NativeMethodsShared.S_OK && runtimeVersion != null)
                {
                    return runtimeVersion.ToString();
                }
                else
                {
                    return String.Empty;
                }
            }
            else
            {
                return ManagedRuntimeVersionReader.GetRuntimeVersion(path);
            }
        }


        /// <summary>
        /// Import assembly dependencies.
        /// </summary>
        /// <returns>The array of assembly dependencies.</returns>
        private AssemblyNameExtension[] ImportAssemblyDependencies()
        {
            ArrayList asmRefs = new ArrayList();

            if (!NativeMethodsShared.IsWindows)
            {
                return _assembly.GetReferencedAssemblies().Select(a => new AssemblyNameExtension(a)).ToArray();
            }

            IntPtr asmRefEnum = IntPtr.Zero;
            UInt32[] asmRefTokens = new UInt32[GENMAN_ENUM_TOKEN_BUF_SIZE];
            UInt32 fetched;
            // Ensure the enum handle is closed.
            try
            {
                // Enum chunks of refs in 16-ref blocks until we run out.
                do
                {
                    _assemblyImport.EnumAssemblyRefs(
                        ref asmRefEnum,
                        asmRefTokens,
                        (uint)asmRefTokens.Length,
                        out fetched);

                    for (uint i = 0; i < fetched; i++)
                    {
                        // Determine the length of the string to contain the name first.
                        IntPtr hashDataPtr, pubKeyPtr;
                        UInt32 hashDataLength, pubKeyBytes, asmNameLength, flags;
                        _assemblyImport.GetAssemblyRefProps(
                            asmRefTokens[i],
                            out pubKeyPtr,
                            out pubKeyBytes,
                            null,
                            0,
                            out asmNameLength,
                            IntPtr.Zero,
                            out hashDataPtr,
                            out hashDataLength,
                            out flags);
                        // Allocate assembly name buffer.
                        char[] asmNameBuf = new char[asmNameLength + 1];
                        IntPtr asmMetaPtr = IntPtr.Zero;
                        // Ensure metadata structure is freed.
                        try
                        {
                            // Allocate metadata structure.
                            asmMetaPtr = AllocAsmMeta();
                            // Retrieve the assembly reference properties.
                            _assemblyImport.GetAssemblyRefProps(
                                asmRefTokens[i],
                                out pubKeyPtr,
                                out pubKeyBytes,
                                asmNameBuf,
                                (uint)asmNameBuf.Length,
                                out asmNameLength,
                                asmMetaPtr,
                                out hashDataPtr,
                                out hashDataLength,
                                out flags);
                            // Construct the assembly name and free metadata structure.
                            AssemblyNameExtension asmName = ConstructAssemblyName(
                                asmMetaPtr,
                                asmNameBuf,
                                asmNameLength,
                                pubKeyPtr,
                                pubKeyBytes,
                                flags);
                            // Add the assembly name to the reference list.
                            asmRefs.Add(asmName);
                        }
                        finally
                        {
                            FreeAsmMeta(asmMetaPtr);
                        }
                    }
                } while (fetched > 0);
            }
            finally
            {
                if (asmRefEnum != IntPtr.Zero)
                    _assemblyImport.CloseEnum(asmRefEnum);
            }

            return (AssemblyNameExtension[])asmRefs.ToArray(typeof(AssemblyNameExtension));
        }

        /// <summary>
        /// Import extra files. These are usually consituent members of a scatter assembly.
        /// </summary>
        /// <returns>The extra files of assembly dependencies.</returns>
        private string[] ImportFiles()
        {
            if (!NativeMethodsShared.IsWindows)
            {
                return new string[0];
            }

            ArrayList files = new ArrayList();
            IntPtr fileEnum = IntPtr.Zero;
            UInt32[] fileTokens = new UInt32[GENMAN_ENUM_TOKEN_BUF_SIZE];
            char[] fileNameBuf = new char[GENMAN_STRING_BUF_SIZE];
            UInt32 fetched;

            // Ensure the enum handle is closed.
            try
            {
                // Enum chunks of files until we run out.
                do
                {
                    _assemblyImport.EnumFiles(ref fileEnum, fileTokens, (uint)fileTokens.Length, out fetched);

                    for (uint i = 0; i < fetched; i++)
                    {
                        IntPtr hashDataPtr;
                        UInt32 fileNameLength, hashDataLength, fileFlags;

                        // Retrieve file properties.
                        _assemblyImport.GetFileProps(fileTokens[i],
                            fileNameBuf, (uint)fileNameBuf.Length, out fileNameLength,
                            out hashDataPtr, out hashDataLength, out fileFlags);

                        // Add file to file list.
                        string file = new string(fileNameBuf, 0, (int)(fileNameLength - 1));
                        files.Add(file);
                    }
                } while (fetched > 0);
            }
            finally
            {
                if (fileEnum != IntPtr.Zero)
                    _assemblyImport.CloseEnum(fileEnum);
            }

            return (string[])files.ToArray(typeof(string));
        }

        /// <summary>
        /// Allocate assembly metadata structure buffer.
        /// </summary>
        /// <returns>Pointer to structure</returns>
        private IntPtr AllocAsmMeta()
        {
            ASSEMBLYMETADATA asmMeta;
            asmMeta.usMajorVersion = asmMeta.usMinorVersion = asmMeta.usBuildNumber = asmMeta.usRevisionNumber = 0;
            asmMeta.cOses = asmMeta.cProcessors = 0;
            asmMeta.rOses = asmMeta.rpProcessors = IntPtr.Zero;
            // Allocate buffer for locale.
            asmMeta.rpLocale = Marshal.AllocCoTaskMem(GENMAN_LOCALE_BUF_SIZE * 2);
            asmMeta.cchLocale = (uint)GENMAN_LOCALE_BUF_SIZE;
            // Convert to unmanaged structure.
            int size = Marshal.SizeOf(typeof(ASSEMBLYMETADATA));
            IntPtr asmMetaPtr = Marshal.AllocCoTaskMem(size);
            Marshal.StructureToPtr(asmMeta, asmMetaPtr, false);

            return asmMetaPtr;
        }

        /// <summary>
        /// Construct assembly name. 
        /// </summary>
        /// <param name="asmMetaPtr">Assembly metadata structure</param>
        /// <param name="asmNameBuf">Buffer containing the name</param>
        /// <param name="asmNameLength">Length of that buffer</param>
        /// <param name="pubKeyPtr">Pointer to public key</param>
        /// <param name="pubKeyBytes">Count of bytes in public key.</param>
        /// <param name="flags">Extra flags</param>
        /// <returns>The assembly name.</returns>
        private AssemblyNameExtension ConstructAssemblyName(IntPtr asmMetaPtr, char[] asmNameBuf, UInt32 asmNameLength, IntPtr pubKeyPtr, UInt32 pubKeyBytes, UInt32 flags)
        {
            // Marshal the assembly metadata back to a managed type.
            ASSEMBLYMETADATA asmMeta = (ASSEMBLYMETADATA)Marshal.PtrToStructure(asmMetaPtr, typeof(ASSEMBLYMETADATA));

            // Construct the assembly name. (Note asmNameLength should/must be > 0.)
            AssemblyName assemblyName = new AssemblyName();
            assemblyName.Name = new string(asmNameBuf, 0, (int)asmNameLength - 1);
            assemblyName.Version = new Version(asmMeta.usMajorVersion, asmMeta.usMinorVersion, asmMeta.usBuildNumber, asmMeta.usRevisionNumber);


            // Set culture info.
            string locale = Marshal.PtrToStringUni(asmMeta.rpLocale);
            if (locale.Length > 0)
            {
                assemblyName.CultureInfo = CultureInfo.CreateSpecificCulture(locale);
            }
            else
            {
                assemblyName.CultureInfo = CultureInfo.CreateSpecificCulture(String.Empty);
            }


            // Set public key or PKT.
            byte[] publicKey = new byte[pubKeyBytes];
            Marshal.Copy(pubKeyPtr, publicKey, 0, (int)pubKeyBytes);
            if ((flags & (uint)CorAssemblyFlags.afPublicKey) != 0)
            {
                assemblyName.SetPublicKey(publicKey);
            }
            else
            {
                assemblyName.SetPublicKeyToken(publicKey);
            }

            assemblyName.Flags = (AssemblyNameFlags)flags;
            return new AssemblyNameExtension(assemblyName);
        }

        /// <summary>
        /// Free the assembly metadata structure.
        /// </summary>
        /// <param name="asmMetaPtr">The pointer.</param>
        private void FreeAsmMeta(IntPtr asmMetaPtr)
        {
            if (asmMetaPtr != IntPtr.Zero)
            {
                // Marshal the assembly metadata back to a managed type.
                ASSEMBLYMETADATA asmMeta = (ASSEMBLYMETADATA)Marshal.PtrToStructure(asmMetaPtr, typeof(ASSEMBLYMETADATA));
                // Free unmanaged memory.
                Marshal.FreeCoTaskMem(asmMeta.rpLocale);
                Marshal.DestroyStructure(asmMetaPtr, typeof(ASSEMBLYMETADATA));
                Marshal.FreeCoTaskMem(asmMetaPtr);
            }
        }
    }

    /// <summary>
    /// Managed implementation of a reader for getting the runtime version of an assembly
    /// </summary>
    static class ManagedRuntimeVersionReader
    {
        class HeaderInfo
        {
            public uint VirtualAddress;
            public uint Size;
            public uint FileOffset;
        }

        /// <summary>
        /// Given a path get the CLR runtime version of the file
        /// </summary>
        /// <param name="path">path to the file</param>
        /// <returns>The CLR runtime version or empty if the path does not exist or the file is not an assembly.</returns>
        public static string GetRuntimeVersion(string path)
        {
            using (var sr = new BinaryReader(File.OpenRead(path)))
            {
                if (!File.Exists(path))
                    return string.Empty;

                // This algorithm for getting the runtime version is based on
                // the ECMA Standard 335: The Common Language Infrastructure (CLI)
                // http://www.ecma-international.org/publications/files/ECMA-ST/ECMA-335.pdf

                try
                {
                    const uint PEHeaderPointerOffset = 0x3c;
                    const uint PEHeaderSize = 20;
                    const uint OptionalPEHeaderSize = 224;
                    const uint OptionalPEPlusHeaderSize = 240;
                    const uint SectionHeaderSize = 40;

                    // The PE file format is specified in section II.25

                    // A PE image starts with an MS-DOS header followed by a PE signature, followed by the PE file header,
                    // and then the PE optional header followed by PE section headers.
                    // There must be room for all of that.

                    if (sr.BaseStream.Length < PEHeaderPointerOffset + 4 + PEHeaderSize + OptionalPEHeaderSize + SectionHeaderSize)
                        return string.Empty;
                    
                    // The PE format starts with an MS-DOS stub of 128 bytes.
                    // At offset 0x3c in the DOS header is a 4-byte unsigned integer offset to the PE
                    // signature (shall be “PE\0\0”), immediately followed by the PE file header

                    sr.BaseStream.Position = PEHeaderPointerOffset;
                    var peHeaderOffset = sr.ReadUInt32();

                    if (peHeaderOffset + 4 + PEHeaderSize + OptionalPEHeaderSize + SectionHeaderSize >= sr.BaseStream.Length)
                        return string.Empty;

                    // The PE header is specified in section II.25.2
                    // Read the PE header signature

                    sr.BaseStream.Position = peHeaderOffset;
                    if (!ReadBytes(sr, (byte)'P', (byte)'E', 0, 0))
                        return string.Empty;

                    // The PE header immediately follows the signature
                    var peHeaderBase = peHeaderOffset + 4;

                    // At offset 2 of the PE header there is the number of sections
                    sr.BaseStream.Position = peHeaderBase + 2;
                    var numberOfSections = sr.ReadUInt16();
                    if (numberOfSections > 96)
                        return string.Empty; // There can't be more than 96 sections, something is wrong

                    // Immediately after the PE Header is the PE Optional Header.
                    // This header is optional in the general PE spec, but always
                    // present in assembly files.
                    // From this header we'll get the CLI header RVA, which is
                    // at offset 208 for PE32, and at offset 224 for PE32+

                    var optionalHeaderOffset = peHeaderBase + PEHeaderSize;

                    uint cliHeaderRvaOffset;
                    uint optionalPEHeaderSize;

                    sr.BaseStream.Position = optionalHeaderOffset;
                    var magicNumber = sr.ReadUInt16();

                    if (magicNumber == 0x10b) // PE32
                    {
                        optionalPEHeaderSize = OptionalPEHeaderSize;
                        cliHeaderRvaOffset = optionalHeaderOffset + 208;
                    }
                    else if (magicNumber == 0x20b) // PE32+
                    {
                        optionalPEHeaderSize = OptionalPEPlusHeaderSize;
                        cliHeaderRvaOffset = optionalHeaderOffset + 224;
                    }
                    else
                        return string.Empty;

                    // Read the CLI header RVA

                    sr.BaseStream.Position = cliHeaderRvaOffset;
                    var cliHeaderRva = sr.ReadUInt32();
                    if (cliHeaderRva == 0)
                        return string.Empty; // No CLI section

                    // Immediately following the optional header is the Section
                    // Table, which contains a number of section headers.
                    // Section headers are specified in section II.25.3

                    // Each section header has the base RVA, size, and file
                    // offset of the section. To find the file offset of the
                    // CLI header we need to find a section that contains
                    // its RVA, and the calculate the file offset using
                    // the base file offset of the section.

                    var sectionOffset = optionalHeaderOffset + optionalPEHeaderSize;

                    // Read all section headers, we need them to make RVA to
                    // offset conversions.

                    var sections = new HeaderInfo [numberOfSections];
                    for (int n = 0; n < numberOfSections; n++)
                    {
                        // At offset 8 of the section is the section size
                        // and base RVA. At offset 20 there is the file offset
                        sr.BaseStream.Position = sectionOffset + 8;
                        var sectionSize = sr.ReadUInt32();
                        var sectionRva = sr.ReadUInt32();
                        sr.BaseStream.Position = sectionOffset + 20;
                        var sectionDataOffset = sr.ReadUInt32();
                        sections[n] = new HeaderInfo {
                            VirtualAddress = sectionRva,
                            Size = sectionSize,
                            FileOffset = sectionDataOffset
                        };
                        sectionOffset += SectionHeaderSize;
                    }

                    uint cliHeaderOffset = RvaToOffset(sections, cliHeaderRva);

                    // CLI section not found
                    if (cliHeaderOffset == 0)
                        return string.Empty;

                    // The CLI header is specified in section II.25.3.3.
                    // It contains all of the runtime-specific data entries and other information.
                    // From the CLI header we need to get the RVA of the metadata root,
                    // which is located at offset 8.

                    sr.BaseStream.Position = cliHeaderOffset + 8;
                    var metadataRva = sr.ReadUInt32();

                    var metadataOffset = RvaToOffset(sections, metadataRva);
                    if (metadataOffset == 0)
                        return string.Empty;

                    // The metadata root is specified in section II.24.2.1
                    // The first 4 bytes contain a signature.
                    // The version string is at offset 12.

                    sr.BaseStream.Position = metadataOffset;
                    if (!ReadBytes(sr, 0x42, 0x53, 0x4a, 0x42)) // Metadata root signature
                        return string.Empty;

                    // Read the version string length
                    sr.BaseStream.Position = metadataOffset + 12;
                    var length = sr.ReadInt32();
                    if (length > 255 || length <= 0 || sr.BaseStream.Position + length >= sr.BaseStream.Length)
                        return string.Empty;

                    // Read the version string
                    var v = Encoding.UTF8.GetString(sr.ReadBytes(length));
                    if (v.Length < 2 || v[0] != 'v')
                        return string.Empty;

                    // Make sure it is a version number
                    Version version;
                    if (!Version.TryParse(v.Substring(1), out version))
                        return string.Empty;
                    return v;
                }
                catch
                {
                    // Something went wrong in spite of all checks. Corrupt file?
                    return string.Empty;
                }
            }
        }

        static bool ReadBytes(BinaryReader r, params byte[] bytes)
        {
            for (int n = 0; n < bytes.Length; n++)
            {
                if (bytes[n] != r.ReadByte())
                    return false;
            }
            return true;
        }

        static uint RvaToOffset(HeaderInfo[] sections, uint rva)
        {
            foreach (var s in sections)
            {
                if (rva >= s.VirtualAddress && rva < s.VirtualAddress + s.Size)
                    return s.FileOffset + (rva - s.VirtualAddress);
            }
            return 0;
        }
    }
}