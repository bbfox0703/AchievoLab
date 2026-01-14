using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CommonUtilities;

namespace RunGame.Utils
{
    /// <summary>
    /// Represents a Valve Data Format (VDF) key-value structure.
    /// Used to parse binary UserGameStatsSchema files containing achievement and statistic definitions.
    /// </summary>
    /// <remarks>
    /// VDF format:
    /// - Binary format with type-tagged values (String, Int32, Float32, Pointer, WideString, Color, UInt64)
    /// - Hierarchical structure with parent-child relationships
    /// - Null-terminated strings (UTF-8 or UTF-16)
    /// - End marker (type byte 0x08) terminates child lists
    /// - Used by Steam for achievement schemas in %STEAM%/appcache/stats/
    /// </remarks>
    public class KeyValue
    {
        private static readonly KeyValue _Invalid = new();

        /// <summary>
        /// Gets or sets the name of this key-value node.
        /// </summary>
        public string Name { get; set; } = "<root>";

        /// <summary>
        /// Gets or sets the value type of this node.
        /// </summary>
        public KeyValueType Type { get; set; } = KeyValueType.None;

        /// <summary>
        /// Gets or sets the value stored in this node (null for container nodes).
        /// </summary>
        public object? Value { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this node contains valid data.
        /// </summary>
        public bool Valid { get; set; }

        /// <summary>
        /// Gets or sets the list of child nodes (null if this is a leaf node).
        /// </summary>
        public List<KeyValue>? Children { get; set; }

        /// <summary>
        /// Gets a child node by name (case-insensitive).
        /// Returns an invalid KeyValue if the child is not found.
        /// </summary>
        /// <param name="key">The child node name.</param>
        /// <returns>The child node, or an invalid KeyValue if not found.</returns>
        public KeyValue this[string key]
        {
            get
            {
                if (Children == null) return _Invalid;

                var child = Children.SingleOrDefault(c => 
                    string.Compare(c.Name, key, StringComparison.InvariantCultureIgnoreCase) == 0);

                return child ?? _Invalid;
            }
        }

        /// <summary>
        /// Converts this node's value to a string.
        /// </summary>
        /// <param name="defaultValue">The default value to return if conversion fails.</param>
        /// <returns>The string value, or the default value if invalid.</returns>
        public string AsString(string defaultValue)
        {
            if (!Valid || Value == null) return defaultValue;
            return Value.ToString()!;
        }

        /// <summary>
        /// Converts this node's value to an integer.
        /// </summary>
        /// <param name="defaultValue">The default value to return if conversion fails.</param>
        /// <returns>The integer value, or the default value if invalid.</returns>
        public int AsInteger(int defaultValue)
        {
            if (!Valid) return defaultValue;

            return Type switch
            {
                KeyValueType.String or KeyValueType.WideString => 
                    int.TryParse((string)Value!, out int value) ? value : defaultValue,
                KeyValueType.Int32 => (int)Value!,
                KeyValueType.Float32 => (int)(float)Value!,
                _ => defaultValue
            };
        }

        /// <summary>
        /// Converts this node's value to a floating-point number.
        /// </summary>
        /// <param name="defaultValue">The default value to return if conversion fails.</param>
        /// <returns>The float value, or the default value if invalid.</returns>
        public float AsFloat(float defaultValue)
        {
            if (!Valid) return defaultValue;

            return Type switch
            {
                KeyValueType.String or KeyValueType.WideString => 
                    float.TryParse((string)Value!, out float value) ? value : defaultValue,
                KeyValueType.Int32 => (float)(int)Value!,
                KeyValueType.Float32 => (float)Value!,
                _ => defaultValue
            };
        }

        /// <summary>
        /// Converts this node's value to a boolean.
        /// </summary>
        /// <param name="defaultValue">The default value to return if conversion fails.</param>
        /// <returns>The boolean value, or the default value if invalid.</returns>
        public bool AsBoolean(bool defaultValue)
        {
            if (!Valid) return defaultValue;

            return Type switch
            {
                KeyValueType.String or KeyValueType.WideString => 
                    bool.TryParse((string)Value!, out bool value) ? value : defaultValue,
                KeyValueType.Int32 => (int)Value! != 0,
                _ => defaultValue
            };
        }

        /// <summary>
        /// Loads a VDF file from disk and parses it as a binary KeyValue structure.
        /// </summary>
        /// <param name="path">The file path to load.</param>
        /// <returns>The parsed KeyValue root node, or null if parsing failed.</returns>
        public static KeyValue? LoadAsBinary(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                
                var kv = new KeyValue 
                { 
                    Valid = true,
                    Type = KeyValueType.None,
                    Children = new List<KeyValue>()
                };
                
                if (kv.ReadAsBinary(fs))
                {
                    return kv;
                }
                
                return null;
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Failed to load KeyValue from '{path}': {ex.GetType().Name} - {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Reads a binary VDF stream and populates this KeyValue node with children.
        /// Recursively parses nested structures until an End marker is encountered.
        /// </summary>
        /// <param name="input">The binary stream to read from.</param>
        /// <returns>True if parsing succeeded; false otherwise.</returns>
        public bool ReadAsBinary(Stream input)
        {
            Children = new List<KeyValue>();
            try
            {
                while (true)
                {
                    var type = (KeyValueType)input.ReadByte();

                    if (type == KeyValueType.End)
                    {
                        break;
                    }

                    var current = new KeyValue
                    {
                        Type = type,
                        Name = input.ReadStringUnicode(),
                    };

                    switch (type)
                    {
                        case KeyValueType.None:
                            current.ReadAsBinary(input);
                            break;

                        case KeyValueType.String:
                            current.Valid = true;
                            current.Value = input.ReadStringUnicode();
                            break;

                        case KeyValueType.Int32:
                            current.Valid = true;
                            current.Value = input.ReadInt32();
                            break;

                        case KeyValueType.UInt64:
                            current.Valid = true;
                            current.Value = input.ReadUInt64();
                            break;

                        case KeyValueType.Float32:
                            current.Valid = true;
                            current.Value = input.ReadSingle();
                            break;

                        case KeyValueType.Pointer:
                            current.Valid = true;
                            current.Value = input.ReadUInt32();
                            break;

                        case KeyValueType.Color:
                            current.Valid = true;
                            current.Value = input.ReadUInt32();
                            break;

                        default:
                            return false;
                    }

                    Children.Add(current);
                }

                Valid = true;
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Failed to read KeyValue binary stream: {ex.GetType().Name} - {ex.Message}");
                return false;
            }
        }

        private static KeyValue? ReadBinaryKeyValue(BinaryReader reader)
        {
            var kv = new KeyValue { Valid = true };
            
            try
            {
                kv.Type = (KeyValueType)reader.ReadByte();
                
                if (kv.Type == KeyValueType.None)
                {
                    return kv;
                }

                kv.Name = ReadNullTerminatedString(reader);

                switch (kv.Type)
                {
                    case KeyValueType.None:
                        kv.Children = new List<KeyValue>();
                        KeyValue? child;
                        while ((child = ReadBinaryKeyValue(reader)) != null && child.Type != KeyValueType.None)
                        {
                            kv.Children.Add(child);
                        }
                        break;

                    case KeyValueType.String:
                        kv.Value = ReadNullTerminatedString(reader);
                        break;

                    case KeyValueType.Int32:
                        kv.Value = reader.ReadInt32();
                        break;

                    case KeyValueType.Float32:
                        kv.Value = reader.ReadSingle();
                        break;

                    case KeyValueType.Pointer:
                        kv.Value = reader.ReadUInt32();
                        break;

                    case KeyValueType.WideString:
                        kv.Value = ReadNullTerminatedWideString(reader);
                        break;

                    case KeyValueType.Color:
                        kv.Value = reader.ReadUInt32();
                        break;

                    case KeyValueType.UInt64:
                        kv.Value = reader.ReadUInt64();
                        break;

                    default:
                        throw new InvalidDataException($"Unknown KeyValue type: {kv.Type}");
                }

                return kv;
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Failed to read binary KeyValue: {ex.GetType().Name} - {ex.Message}");
                return null;
            }
        }

        private static string ReadNullTerminatedString(BinaryReader reader)
        {
            var bytes = new List<byte>();
            byte b;
            while ((b = reader.ReadByte()) != 0)
            {
                bytes.Add(b);
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private static string ReadNullTerminatedWideString(BinaryReader reader)
        {
            var chars = new List<char>();
            char c;
            while ((c = (char)reader.ReadUInt16()) != 0)
            {
                chars.Add(c);
            }
            return new string(chars.ToArray());
        }
    }

    /// <summary>
    /// Enumeration of VDF value types.
    /// </summary>
    public enum KeyValueType : byte
    {
        /// <summary>
        /// Container node with children (no value).
        /// </summary>
        None = 0,

        /// <summary>
        /// UTF-8 null-terminated string.
        /// </summary>
        String = 1,

        /// <summary>
        /// 32-bit signed integer.
        /// </summary>
        Int32 = 2,

        /// <summary>
        /// 32-bit floating-point number.
        /// </summary>
        Float32 = 3,

        /// <summary>
        /// 32-bit pointer value.
        /// </summary>
        Pointer = 4,

        /// <summary>
        /// UTF-16 null-terminated wide string.
        /// </summary>
        WideString = 5,

        /// <summary>
        /// 32-bit color value (RGBA).
        /// </summary>
        Color = 6,

        /// <summary>
        /// 64-bit unsigned integer.
        /// </summary>
        UInt64 = 7,

        /// <summary>
        /// End marker indicating the end of a child list.
        /// </summary>
        End = 8
    }

    /// <summary>
    /// Extension methods for reading VDF binary data from streams.
    /// </summary>
    public static class StreamExtensions
    {
        /// <summary>
        /// Reads a null-terminated UTF-8 string from the stream.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The decoded string.</returns>
        public static string ReadStringUnicode(this Stream stream)
        {
            var bytes = new List<byte>();
            byte b;
            while ((b = (byte)stream.ReadByte()) != 0)
            {
                bytes.Add(b);
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        /// <summary>
        /// Reads a 32-bit signed integer from the stream (little-endian).
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The integer value.</returns>
        public static int ReadInt32(this Stream stream)
        {
            var bytes = new byte[4];
            stream.ReadExactly(bytes, 0, 4);
            return BitConverter.ToInt32(bytes, 0);
        }

        /// <summary>
        /// Reads a 32-bit unsigned integer from the stream (little-endian).
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The unsigned integer value.</returns>
        public static uint ReadUInt32(this Stream stream)
        {
            var bytes = new byte[4];
            stream.ReadExactly(bytes, 0, 4);
            return BitConverter.ToUInt32(bytes, 0);
        }

        /// <summary>
        /// Reads a 64-bit unsigned integer from the stream (little-endian).
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The unsigned long value.</returns>
        public static ulong ReadUInt64(this Stream stream)
        {
            var bytes = new byte[8];
            stream.ReadExactly(bytes, 0, 8);
            return BitConverter.ToUInt64(bytes, 0);
        }

        /// <summary>
        /// Reads a 32-bit floating-point number from the stream (little-endian).
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The float value.</returns>
        public static float ReadSingle(this Stream stream)
        {
            var bytes = new byte[4];
            stream.ReadExactly(bytes, 0, 4);
            return BitConverter.ToSingle(bytes, 0);
        }
    }
}
