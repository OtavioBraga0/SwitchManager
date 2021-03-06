﻿using log4net;
using SwitchManager.io;
using SwitchManager.util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace SwitchManager.nx.system
{
    /// <summary>
    /// Represents a CNMT file, which is a type of metadata file for NSPs (I think).
    /// See http://switchbrew.org/index.php?title=NCA
    /// </summary>
    [XmlRoot("ContentMeta", Namespace = null, IsNullable = false)]
    public class CNMT : IDisposable
    {
        private static readonly ILog logger = LogManager.GetLogger(typeof(CNMT));

        // below are XML attributes
        [XmlElement(ElementName = "Type")]
        public TitleType Type { get; set; }

        [XmlElement(ElementName = "Id")]
        public string XmlId {
            get { return "0x" + Id.ToLower(); }
            set
            {
                string id = value.StartsWith("0x") ? value.Substring(2) : value;
                if (id?.Length < 16) throw new Exception("Couldn't read CNMT XmlId from string");
                this.Id = id?.Substring(0, 16)?.ToLower();
            }
        }

        [XmlElement(ElementName = "Version")]
        private uint version;
        public uint Version { get { return version; } set { this.version = value & 0xFFFF0000; } }

        [XmlElement(ElementName = "RequiredDownloadSystemVersion")]
        public string RequiredDownloadSystemVersion { get; set; }

        [XmlElement(ElementName = "Content")]
        public CnmtContentEntry[] Content
        {
            get
            {
                this.parsedContent = ParseContent();

                return parsedContent.Values.ToArray();

            }
            set
            {
                this.parsedContent = new Dictionary<string, CnmtContentEntry>(value.Length);
                foreach (var entry in value)
                {
                    parsedContent.Add(entry.Id, entry);
                }
            }
        }
        private Dictionary<string, CnmtContentEntry> parsedContent;

        private Dictionary<string, CnmtMetaEntry> parsedMeta;

        [XmlElement(ElementName = "Digest")]
        public string Digest
        {
            get { return Miscellaneous.BytesToHex(Hash); }
            set
            {
                if (value.Length != 64) throw new Exception("Coudn't read CNMT Digest from string");
                this.Hash = Miscellaneous.HexToBytes(value);
            }
        }

        [XmlElement(ElementName = "KeyGenerationMin")]
        public byte MasterKeyRevision { get; set; }

        [XmlIgnore]
        public long? RequiredVersion { get; set; }

        [XmlElement(ElementName = "RequiredSystemVersion")]
        public long? RequiredSystemVersion
        {
            get => SwitchTitle.IsDLCID(Id) ? null : RequiredVersion;
            set { }
        }

        [XmlElement(ElementName = "RequiredApplicationVersion")]
        public long? RequiredApplicationVersion
        {
            get => SwitchTitle.IsDLCID(Id) ? RequiredVersion: null;
            set { }
        }

        [XmlElement(ElementName = "PatchId")]
        public string PatchId
        {
            get => SwitchTitle.IsBaseGameID(Id) ? "0x" + SwitchTitle.GetUpdateIDFromBaseGame(Id) : null;
            
            set { }
        }

        [XmlElement(ElementName = "OriginalId")]
        public string OriginalId
        {
            get => SwitchTitle.IsUpdateTitleID(Id) ? "0x" + SwitchTitle.GetBaseGameIDFromUpdate(Id)?.ToLower() : null;
            
            set { }
        }

        [XmlElement(ElementName = "ApplicationId")]
        public string ApplicationId
        {
            get => SwitchTitle.IsDLCID(Id) ? "0x" + SwitchTitle.GetBaseGameIDFromDLC(Id)?.ToLower() : null;
            
            set { }
        }

        [XmlIgnore]
        public string Id { get; set; }
        [XmlIgnore]
        public string CnmtFilePath { get; set; }
        [XmlIgnore]
        public string CnmtDirectory { get; set; }
        [XmlIgnore]
        public string CnmtNcaFilePath { get; set; }
        [XmlIgnore]
        public string CnmtXmlFilePath { get; set; }


        private ushort tableOffset;
        private ushort numContentEntries;
        private ushort numMetaEntries;
        public byte[] Hash { get; set; }

        /// <summary>
        /// Constructor for reading from XML or building up from scratch using data rather than files on disk
        /// </summary>
        public CNMT()
        {
        }

        /// <summary>
        /// Builds an XML from a CNMT on disk.
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="headerPath"></param>
        /// <param name="cnmtDir"></param>
        /// <param name="ncaPath"></param>
        public CNMT(string filePath, string headerPath, string cnmtDir, string ncaPath)
        {
            this.CnmtFilePath = filePath;
            this.CnmtDirectory = cnmtDir;
            this.CnmtNcaFilePath = ncaPath;

            FileStream fs = File.OpenRead(filePath);
            BinaryReader br = new BinaryReader(fs);

            // Reading CNMT file
            // See http://switchbrew.org/index.php?title=NCA#Metadata_file

            // Title ID is at offset 0 and is 8 bytes long
            this.Id = $"{br.ReadUInt64():x16}";

            // Title Version is immediately after at offset 8 and is 4 bytes long
            this.Version = br.ReadUInt32();

            // Title Type is immediately after at offset 0xC and is 1 byte long
            // See http://switchbrew.org/index.php?title=NCM_services#Title_Types
            this.Type = (TitleType)br.ReadByte();

            // What's at 0xD?
            br.ReadByte();

            // Offset to table relative to the end of this 0x20-byte header, whatever that means
            this.tableOffset = br.ReadUInt16();

            // Number of content entries, offset 0x10, 2 bytes long
            this.numContentEntries = br.ReadUInt16();

            // Number of meta entries, offset 0x12, 2 bytes long
            this.numMetaEntries = br.ReadUInt16();

            // I see this in CDNSP but I don't see it in the doc
            // This is the 8 bytes before the app header starts at 0x20
            // The docs give no information about the 12 bytes starting at 0x14 (up to 0x20)
            br.BaseStream.Seek(0x18, SeekOrigin.Begin); this.RequiredDownloadSystemVersion = br.ReadUInt64().ToString();

            // Minimum System Version is at offset 0x28 and is 8 bytes long
            br.BaseStream.Seek(0x28, SeekOrigin.Begin); this.RequiredVersion = br.ReadInt64();

            // Get the hash/digest from the last 0x20 bytes
            br.BaseStream.Seek(-0x20, SeekOrigin.End); this.Hash = br.ReadBytes(0x20);
            
            br.Close();

            fs = File.OpenRead(headerPath);
            br = new BinaryReader(fs);
            // Grabs the first 0x220 bytes from Header.bin and stores them for later
            // See http://switchbrew.org/index.php?title=NCA_Format#Header
            // This is used later in the rights ID
            br.BaseStream.Seek(0x220, SeekOrigin.Begin);
            this.MasterKeyRevision = br.ReadByte();
            br.Close();
        }

        public Dictionary<string, CnmtMetaEntry> ParseSystemUpdate()
        {
            if (parsedMeta == null)
            {
                parsedMeta = new Dictionary<string, CnmtMetaEntry>();

                // System updates are different and get their own parser
                // Don't try to call parsesystemupdate if it isn't one!
                // This was the easiest solution to coding a file with drastically different file formats
                // in a strongly typed language.
                // I also considered storing everything in a dictionary of strings, or in a generic json container
                // but I ultimately settled on having one parse for system update that handles system updates and
                // one that handles other stuff
                // I might even have two different CNMTs with different parse methods, subclassing CNMT, I dunno
                if (this.Type != TitleType.SystemUpdate)
                    throw new Exception("Do not call ParseSystemUpdate unless the CNMT is for a System Update title!!!");

                FileStream fs = File.OpenRead(CnmtFilePath);
                BinaryReader br = new BinaryReader(fs);

                // Reach each entry, starting at 0x20 (after the header ends)
                // each entry is 0x10 in size, and their are numMetaEntries (stored at 0x12) of them
                // With patch, update and addon types, there's a secondary header from 0x20 to 0x30
                // 0xE tells you how many bytes to skip from 0x20 to the start of the content entries and 0x10 says how many there are
                // With System Update, 0xE is blank, but 0x12 tells you how many metadata entries there are, starting at 0x20
                // See http://switchbrew.org/index.php?title=NCA
                br.BaseStream.Seek(0x20, SeekOrigin.Begin);
                for (int i = 0; i < this.numMetaEntries; i++)
                {
                    var meta = new CnmtMetaEntry();
                    // Parse a meta entry
                    meta.TitleID = $"{br.ReadUInt64():X16}"; // Title ID, offset 0x0, 8 bytes, we store it in hex to match other title ids
                    meta.Version = br.ReadUInt32(); // Version, offset 0x8, 4 bytes, store it as uint to match other versions, even though in hex it is always in the format 0xN0000, where N is the version number OMG why don't I fucking store this shit in hex? oh well whatever
                    meta.Type = (TitleType)br.ReadByte(); // Title Type, offset 0xC, 1 byte
                    meta.Flag = br.ReadByte(); // Unknown flag bit?, not using, offset 0xD, 1 byte
                    meta.Unknown = br.ReadUInt16(); // Unused? Skip, offset 0xE, 2 bytes
                    // restart loop 0x10 higher than the last loop read to read next meta entry
                    parsedMeta[meta.TitleID] = meta;
                }
            }
            return parsedMeta;
        }

        /// <summary>
        /// Parses a list of NCA file names that match the given NCA type
        /// </summary>
        /// <param name="ncaType"></param>
        /// <returns></returns>
        public List<string> ParseNCAs(NCAType desiredType)
        {
            // A simplified parse function that only gets the list of NCA files of the given type
            if (this.Type == TitleType.SystemUpdate)
                throw new Exception("Do not call Parse on a System Update title!!! Only for other types!");

            var content = Content.Where(e => e.Type == desiredType).Select(e => e.Id);
            return content.ToList();
            /*
            var data = new List<string>();
            FileStream fs = File.OpenRead(this.CnmtFilePath);
            BinaryReader br = new BinaryReader(fs);

            // Reach each content entry, starting at 0x20 + tableOffset
            // each entry is 0x38 in size, and their are nEntries of them
            // With patch, update and addon types, there's a secondary header from 0x20 to 0x30
            // 0xE tells you how many bytes to skip from 0x20 to the start of the content entries and 0x10 says how many there are
            br.BaseStream.Seek(0x20 + this.tableOffset, SeekOrigin.Begin);
            for (int i = 0; i < this.numContentEntries; i++)
            {
                // Parse a content entry
                br.ReadBytes(0x20); // Hash, offset 0x0, 32 bytes
                string NcaId = Miscellaneous.BytesToHex(br.ReadBytes(0x10)); // NCA ID, offset 0x20, 16 bytes, convert bytes to a hex string
                br.ReadBytes(6);
                NCAType type = (NCAType)br.ReadByte(); // Type (0=meta, 1=program, 2=data, 3=control, 4=offline-manual html, 5=legal html, 6=game-update RomFS patches?), offset 0x36, 1 byte
                br.ReadByte(); // Unknown, offset 0x37, 1 byte

                // Only keep entries of the type we care about, or keep all of them if no type was specified
                if (type == desiredType)
                {
                    data.Add(NcaId);
                }
                // restart loop 0x38 higher than the last loop read to read next meta entry
            }
            br.Close();
            return data;
            */
        }

        public Dictionary<string, CnmtContentEntry> ParseContent(NCAType? ncaType = null)
        {
            if (parsedContent == null)
            {
                parsedContent = new Dictionary<string, CnmtContentEntry>();

                // First thing - add the content entry for THIS, the CNMT entry (called "Meta" type)
                var meta = new CnmtContentEntry
                {
                    Type = NCAType.Meta,
                    Id = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(this.CnmtNcaFilePath)),
                    Size = FileUtils.GetFileSystemSize(this.CnmtNcaFilePath) ?? 0,
                    MasterKeyRevision = MasterKeyRevision
                };
                using (FileStream stream = File.OpenRead(this.CnmtNcaFilePath))
                {
                    meta.HashData = new SHA256Managed().ComputeHash(stream);
                }
                parsedContent.Add(meta.Id, meta);

                // ParseContent is for everything other than system updates
                // This might be more elegant with subclasses or an interface or something,
                // but I would still come right back to having to have a single data type returned by the
                // virtual function that encompassed both content and metadata entries, and I just don't want to
                // They are completely different and shouldn't be combined into one class
                if (this.Type == TitleType.SystemUpdate)
                    throw new Exception("Do not call Parse on a System Update title!!! Only for other types!");

                using (BinaryReader br = new BinaryReader(File.OpenRead(this.CnmtFilePath)))
                {
                    // Reach each content entry, starting at 0x20 + tableOffset
                    // each entry is 0x38 in size, and their are nEntries of them
                    // With patch, update and addon types, there's a secondary header from 0x20 to 0x30
                    // 0xE tells you how many bytes to skip from 0x20 to the start of the content entries and 0x10 says how many there are
                    br.BaseStream.Seek(0x20 + this.tableOffset, SeekOrigin.Begin);
                    for (int i = 0; i < this.numContentEntries; i++)
                    {
                        // Parse a content entry
                        var content = new CnmtContentEntry();
                        content.HashData = br.ReadBytes(0x20); // Hash, offset 0x0, 32 bytes
                        content.Id = Miscellaneous.BytesToHex(br.ReadBytes(0x10)); // NCA ID, offset 0x20, 16 bytes, convert bytes to a hex string
                        byte[] sizeBuffer = new byte[8];
                        br.Read(sizeBuffer, 0, 6);
                        content.Size = BitConverter.ToInt64(sizeBuffer, 0); // Size, offset 0x30, 6 bytes (8 byte long converted from only 6 bytes)
                        content.Type = (NCAType)br.ReadByte(); // Type (0=meta, 1=program, 2=data, 3=control, 4=offline-manual html, 5=legal html, 6=game-update RomFS patches?), offset 0x36, 1 byte
                        content.IdOffset = br.ReadByte(); // IdOffset, offset 0x37, 1 byte
                        content.MasterKeyRevision = this.MasterKeyRevision;

                        parsedContent[content.Id] = content;
                        // restart loop 0x38 higher than the last loop read to read next meta entry
                    }
                }
            }

            if (!ncaType.HasValue)
                return parsedContent;
            else
                return parsedContent.Where(e => e.Value.Type == ncaType).ToDictionary(e => e.Key, e => e.Value);
        }

        /// <summary>
        /// Outputs this CNMT to XML. If outFile is missing, creates the XML using the same file name as the
        /// CNMT nca, but with an extension of xml instead of nca.
        /// </summary>
        /// <param name="outFile"></param>
        /// <returns></returns>
        public string GenerateXml(string outFile = null)
        {
            if (outFile == null)
                outFile = CnmtXmlFilePath ?? Path.GetFullPath(CnmtNcaFilePath).Replace(".nca", ".xml");

            // Create a new file stream to write the serialized object to a file
            using (FileStream str = FileUtils.OpenWriteStream(outFile))
            {
                XmlSerializer xmls = new XmlSerializer(typeof(CNMT));
                XmlSerializerNamespaces ns = new XmlSerializerNamespaces();
                ns.Add("", "");
                xmls.Serialize(str, this, ns);
            }

            logger.Info($"Generated XML file {Path.GetFileName(outFile)}!");
            return outFile;
        }

        public static async Task<CNMT> FromNca(string cnmtNCA)
        {
            DirectoryInfo cnmtDir = await Hactool.DecryptNCA(cnmtNCA).ConfigureAwait(false);

            // For CNMTs, there is a section0 containing a single cnmt file, plus a Header.bin right next to section0
            var sectionDirInfo = cnmtDir.EnumerateDirectories("section0").First();
            var extractedCnmt = sectionDirInfo.EnumerateFiles().First();
            var headerFile = cnmtDir.EnumerateFiles("Header.bin").First();

            return new CNMT(extractedCnmt.FullName, headerFile.FullName, cnmtDir.FullName, cnmtNCA);
        }

        /// <summary>
        /// Generates a CNMT based on an XML representation of the object.
        /// </summary>
        /// <param name="inFile"></param>
        /// <returns></returns>
        public static CNMT FromXml(string inFile)
        {
            using (TextReader reader = new StreamReader(inFile))
            {
                XmlSerializer xmls = new XmlSerializer(typeof(CNMT));
                CNMT cnmt = xmls.Deserialize(reader) as CNMT;
                cnmt.CnmtXmlFilePath = inFile;

                string nca = inFile.Replace("xml", "nca");
                if (FileUtils.FileExists(nca))
                    cnmt.CnmtNcaFilePath = nca;
                return cnmt;
            }
        }

        public void DeleteDirectory()
        {
            if (this.CnmtDirectory != null)
                FileUtils.DeleteDirectory(this.CnmtDirectory, true);
        }

        public void Dispose()
        {
            DeleteDirectory();
        }
    }
}
