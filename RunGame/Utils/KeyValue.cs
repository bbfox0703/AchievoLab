using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CommonUtilities;

namespace RunGame.Utils
{
    public class KeyValue
    {
        private static readonly KeyValue _Invalid = new();
        
        public string Name { get; set; } = "<root>";
        public KeyValueType Type { get; set; } = KeyValueType.None;
        public object? Value { get; set; }
        public bool Valid { get; set; }
        public List<KeyValue>? Children { get; set; }

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

        public string AsString(string defaultValue)
        {
            if (!Valid || Value == null) return defaultValue;
            return Value.ToString()!;
        }

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
                DebugLogger.LogDebug($"Failed to load KeyValue from '{path}': {ex.GetType().Name} - {ex.Message}");
                return null;
            }
        }

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
                DebugLogger.LogDebug($"Failed to read KeyValue binary stream: {ex.GetType().Name} - {ex.Message}");
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
                DebugLogger.LogDebug($"Failed to read binary KeyValue: {ex.GetType().Name} - {ex.Message}");
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

    public enum KeyValueType : byte
    {
        None = 0,
        String = 1,
        Int32 = 2,
        Float32 = 3,
        Pointer = 4,
        WideString = 5,
        Color = 6,
        UInt64 = 7,
        End = 8
    }

    public static class StreamExtensions
    {
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

        public static int ReadInt32(this Stream stream)
        {
            var bytes = new byte[4];
            stream.ReadExactly(bytes, 0, 4);
            return BitConverter.ToInt32(bytes, 0);
        }

        public static uint ReadUInt32(this Stream stream)
        {
            var bytes = new byte[4];
            stream.ReadExactly(bytes, 0, 4);
            return BitConverter.ToUInt32(bytes, 0);
        }

        public static ulong ReadUInt64(this Stream stream)
        {
            var bytes = new byte[8];
            stream.ReadExactly(bytes, 0, 8);
            return BitConverter.ToUInt64(bytes, 0);
        }

        public static float ReadSingle(this Stream stream)
        {
            var bytes = new byte[4];
            stream.ReadExactly(bytes, 0, 4);
            return BitConverter.ToSingle(bytes, 0);
        }
    }
}