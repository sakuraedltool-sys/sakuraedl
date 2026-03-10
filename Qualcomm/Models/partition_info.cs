// ============================================================================
// SakuraEDL - Partition Info Model | 分区信息模型
// ============================================================================
// [ZH] 分区信息 - 存储分区的 LUN、名称、大小、扇区等信息
// [EN] Partition Info - Store partition LUN, name, size, sector info
// [JA] パーティション情報 - LUN、名前、サイズ、セクタ情報を保存
// [KO] 파티션 정보 - LUN, 이름, 크기, 섹터 정보 저장
// [RU] Информация о разделе - Хранение LUN, имени, размера, секторов
// [ES] Info de partición - Almacenar LUN, nombre, tamaño, sectores
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | MIT License
// ============================================================================

using System;
using System.ComponentModel;
using System.IO;

namespace SakuraEDL.Qualcomm.Models
{
    /// <summary>
    /// 分区信息模型
    /// </summary>
    public class PartitionInfo : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        
        protected void OnPropertyChanged(string name)
        {
            if (PropertyChanged != null)
                PropertyChanged(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>
        /// LUN (Logical Unit Number)
        /// </summary>
        public int Lun { get; set; }

        /// <summary>
        /// 分区名称
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// 起始扇区
        /// </summary>
        public long StartSector { get; set; }

        /// <summary>
        /// 扇区数量
        /// </summary>
        public long NumSectors { get; set; }

        /// <summary>
        /// 扇区大小 (通常为 512 或 4096)
        /// </summary>
        public int SectorSize { get; set; }

        /// <summary>
        /// 分区大小 (字节)
        /// </summary>
        public long Size
        {
            get { return NumSectors * SectorSize; }
        }

        /// <summary>
        /// 分区类型 GUID
        /// </summary>
        public string TypeGuid { get; set; }

        /// <summary>
        /// 分区唯一 GUID
        /// </summary>
        public string UniqueGuid { get; set; }

        /// <summary>
        /// 分区属性
        /// </summary>
        public ulong Attributes { get; set; }

        /// <summary>
        /// GPT 条目索引 (用于 patch 操作)
        /// </summary>
        public int EntryIndex { get; set; } = -1;

        /// <summary>
        /// GPT 条目起始扇区 (通常为 2)
        /// </summary>
        public long GptEntriesStartSector { get; set; } = 2;

        private bool _isSelected;
        /// <summary>
        /// 是否选中 (用于 UI)
        /// </summary>
        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged("IsSelected");
                }
            }
        }

        private string _customFilePath = "";
        /// <summary>
        /// 自定义刷写文件路径
        /// </summary>
        public string CustomFilePath
        {
            get { return _customFilePath; }
            set
            {
                if (_customFilePath != value)
                {
                    _customFilePath = value ?? "";
                    OnPropertyChanged("CustomFilePath");
                    OnPropertyChanged("CustomFileName");
                    OnPropertyChanged("HasCustomFile");
                }
            }
        }

        /// <summary>
        /// 自定义文件名
        /// </summary>
        public string CustomFileName
        {
            get { return string.IsNullOrEmpty(_customFilePath) ? "" : Path.GetFileName(_customFilePath); }
        }

        /// <summary>
        /// 是否有自定义文件
        /// </summary>
        public bool HasCustomFile
        {
            get { return !string.IsNullOrEmpty(_customFilePath); }
        }

        /// <summary>
        /// 结束扇区
        /// </summary>
        public long EndSector
        {
            get { return StartSector + NumSectors - 1; }
        }

        /// <summary>
        /// 格式化的大小字符串 (不足1MB按KB，满1GB按GB)
        /// </summary>
        public string FormattedSize
        {
            get
            {
                var size = Size;
                if (size >= 1024L * 1024 * 1024)
                    return string.Format("{0:F2} GB", size / (1024.0 * 1024 * 1024));
                if (size >= 1024 * 1024)
                    return string.Format("{0:F2} MB", size / (1024.0 * 1024));
                if (size >= 1024)
                    return string.Format("{0:F0} KB", size / 1024.0);
                return string.Format("{0} B", size);
            }
        }

        /// <summary>
        /// 位置信息
        /// </summary>
        public string Location
        {
            get { return string.Format("0x{0:X} - 0x{1:X}", StartSector, EndSector); }
        }

        public PartitionInfo()
        {
            Name = "";
            SectorSize = 512;
            TypeGuid = "";
            UniqueGuid = "";
        }

        public override string ToString()
        {
            return string.Format("[LUN{0}] {1}: {2} ({3} - {4})", Lun, Name, FormattedSize, StartSector, EndSector);
        }
    }

    /// <summary>
    /// 刷写分区信息 (用于刷写操作)
    /// </summary>
    public class FlashPartitionInfo
    {
        public string Lun { get; set; }
        public string Name { get; set; }
        public string StartSector { get; set; }
        public long NumSectors { get; set; }
        public string Filename { get; set; }
        public long FileOffset { get; set; }
        public bool IsSparse { get; set; }

        public FlashPartitionInfo()
        {
            Lun = "0";
            Name = "";
            StartSector = "0";
            Filename = "";
        }

        public FlashPartitionInfo(string lun, string name, string start, long sectors, string filename = "", long offset = 0)
        {
            Lun = lun;
            Name = name;
            StartSector = start;
            NumSectors = sectors;
            Filename = filename ?? "";
            FileOffset = offset;
        }
    }
}
