using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTDB.Allocators;
using BTDB.BTreeLib;
using BTDB.Buffer;
using BTDB.Collections;
using BTDB.KVDBLayer.BTree;
using BTDB.KVDBLayer.Implementation;
using BTDB.ODBLayer;
using BTDB.StreamLayer;

namespace BTDB.KVDBLayer
{
    public class BTreeKeyValueDB : IHaveSubDB, IKeyValueDBInternal
    {
        const int MaxValueSizeInlineInMemory = 7;
        const int EndOfIndexFileMarker = 0x1234DEAD;
        internal IRootNode _lastCommitted;
        readonly ConcurrentBag<IRootNode> _waitingToDispose = new ConcurrentBag<IRootNode>();

        readonly ConcurrentDictionary<IRootNode, bool> _usedNodesInReadonlyTransactions =
            new ConcurrentDictionary<IRootNode, bool>(ReferenceEqualityComparer<IRootNode>.Instance);

        // it is long only because Interlock.Read is just long capable, MaxValue means no preserving history
        long _preserveHistoryUpToCommitUlong;

        IRootNode? _nextRoot;
        BTreeKeyValueDBTransaction? _writingTransaction;

        readonly Queue<TaskCompletionSource<IKeyValueDBTransaction>> _writeWaitingQueue =
            new Queue<TaskCompletionSource<IKeyValueDBTransaction>>();

        readonly object _writeLock = new object();
        uint _fileIdWithTransactionLog;
        uint _fileIdWithPreviousTransactionLog;
        IFileCollectionFile? _fileWithTransactionLog;
        AbstractBufferedWriter? _writerWithTransactionLog;
        static readonly byte[] MagicStartOfTransaction = {(byte) 't', (byte) 'R'};
        public long MaxTrLogFileSize { get; set; }
        public ulong CompactorReadBytesPerSecondLimit { get; }
        public ulong CompactorWriteBytesPerSecondLimit { get; }

        readonly IOffHeapAllocator _allocator;
        readonly ICompressionStrategy _compression;
        readonly ICompactorScheduler? _compactorScheduler;

        readonly IFileCollectionWithFileInfos _fileCollection;
        readonly Dictionary<long, object> _subDBs = new Dictionary<long, object>();
        readonly Func<CancellationToken, bool>? _compactFunc;
        readonly bool _readOnly;

        public BTreeKeyValueDB(IFileCollection fileCollection)
            : this(fileCollection, new SnappyCompressionStrategy())
        {
        }

        public BTreeKeyValueDB(IFileCollection fileCollection, ICompressionStrategy compression,
            uint fileSplitSize = int.MaxValue)
            : this(fileCollection, compression, fileSplitSize, CompactorScheduler.Instance)
        {
        }

        public BTreeKeyValueDB(IFileCollection fileCollection, ICompressionStrategy compression, uint fileSplitSize,
            ICompactorScheduler? compactorScheduler)
            : this(new KeyValueDBOptions
            {
                Allocator = new MallocAllocator(),
                FileCollection = fileCollection,
                Compression = compression,
                FileSplitSize = fileSplitSize,
                CompactorScheduler = compactorScheduler
            })
        {
        }

        public BTreeKeyValueDB(KeyValueDBOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.FileCollection == null) throw new ArgumentNullException(nameof(options.FileCollection));
            if (options.FileSplitSize < 1024 || options.FileSplitSize > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(options.FileSplitSize), "Allowed range 1024 - 2G");
            _compactorScheduler = options.CompactorScheduler;
            MaxTrLogFileSize = options.FileSplitSize;
            _readOnly = options.ReadOnly;
            _compression = options.Compression ?? throw new ArgumentNullException(nameof(options.Compression));
            DurableTransactions = false;
            _fileCollection = new FileCollectionWithFileInfos(options.FileCollection);
            CompactorReadBytesPerSecondLimit = options.CompactorReadBytesPerSecondLimit ?? 0;
            CompactorWriteBytesPerSecondLimit = options.CompactorWriteBytesPerSecondLimit ?? 0;
            _allocator = options.Allocator ?? new MallocAllocator();
            _lastCommitted = BTreeImpl12.CreateEmptyRoot(_allocator);
            _lastCommitted.Commit();
            _preserveHistoryUpToCommitUlong = (long) (options.PreserveHistoryUpToCommitUlong ?? ulong.MaxValue);
            LoadInfoAboutFiles(options.OpenUpToCommitUlong);
            if (!_readOnly)
            {
                _compactFunc = _compactorScheduler?.AddCompactAction(Compact);
                _compactorScheduler?.AdviceRunning(true);
            }
        }

        public ulong DistanceFromLastKeyIndex(IRootNodeInternal root)
        {
            return DistanceFromLastKeyIndex((IRootNode) root);
        }

        Span<KeyIndexInfo> IKeyValueDBInternal.BuildKeyIndexInfos()
        {
            return BuildKeyIndexInfos();
        }

        uint IKeyValueDBInternal.CalculatePreserveKeyIndexKeyFromKeyIndexInfos(ReadOnlySpan<KeyIndexInfo> keyIndexes)
        {
            return CalculatePreserveKeyIndexKeyFromKeyIndexInfos(keyIndexes);
        }

        public uint GetTrLogFileId(IRootNodeInternal root)
        {
            return ((IRootNode) root).TrLogFileId;
        }

        public void IterateRoot(IRootNodeInternal root, ValuesIterateAction visit)
        {
            ((IRootNode) root).ValuesIterate(visit);
        }

        internal Span<KeyIndexInfo> BuildKeyIndexInfos()
        {
            var keyIndexes = new StructList<KeyIndexInfo>();
            foreach (var fileInfo in _fileCollection.FileInfos)
            {
                var keyIndex = fileInfo.Value as IKeyIndex;
                if (keyIndex == null) continue;
                keyIndexes.Add(new KeyIndexInfo
                    {Key = fileInfo.Key, Generation = keyIndex.Generation, CommitUlong = keyIndex.CommitUlong});
            }

            if (keyIndexes.Count > 1)
                keyIndexes.Sort(Comparer<KeyIndexInfo>.Create((l, r) => Comparer<long>.Default.Compare(l.Generation, r.Generation)));
            return keyIndexes.AsSpan();
        }

        void LoadInfoAboutFiles(ulong? openUpToCommitUlong)
        {
            long latestGeneration = -1;
            uint latestTrLogFileId = 0;
            foreach (var fileInfo in _fileCollection.FileInfos)
            {
                if (!(fileInfo.Value is IFileTransactionLog trLog)) continue;
                if (trLog.Generation > latestGeneration)
                {
                    latestGeneration = trLog.Generation;
                    latestTrLogFileId = fileInfo.Key;
                }
            }

            var keyIndexes = BuildKeyIndexInfos();
            var preserveKeyIndexKey = CalculatePreserveKeyIndexKeyFromKeyIndexInfos(keyIndexes);
            var preserveKeyIndexGeneration = CalculatePreserveKeyIndexGeneration(preserveKeyIndexKey);
            var firstTrLogId = LinkTransactionLogFileIds(latestTrLogFileId);
            var firstTrLogOffset = 0u;
            var hasKeyIndex = false;
            try
            {
                while (keyIndexes.Length > 0)
                {
                    var nearKeyIndex = keyIndexes.Length - 1;
                    if (openUpToCommitUlong.HasValue)
                    {
                        while (nearKeyIndex >= 0)
                        {
                            if (keyIndexes[nearKeyIndex].CommitUlong <= openUpToCommitUlong.Value)
                                break;
                            nearKeyIndex--;
                        }

                        if (nearKeyIndex < 0)
                        {
                            // If we have all trl files we can replay from start
                            if (GetGeneration(firstTrLogId) == 1)
                                break;
                            // Or we have to start with oldest kvi
                            nearKeyIndex = 0;
                        }
                    }

                    var keyIndex = keyIndexes[nearKeyIndex];
                    keyIndexes.Slice(nearKeyIndex + 1).CopyTo(keyIndexes.Slice(nearKeyIndex));
                    keyIndexes = keyIndexes.Slice(0, keyIndexes.Length - 1);
                    var info = (IKeyIndex) _fileCollection.FileInfoByIdx(keyIndex.Key);
                    _nextRoot = _lastCommitted.CreateWritableTransaction();
                    try
                    {
                        if (LoadKeyIndex(keyIndex.Key, info!) && firstTrLogId <= info.TrLogFileId)
                        {
                            _lastCommitted.Dispose();
                            _lastCommitted = _nextRoot!;
                            _lastCommitted!.Commit();
                            _nextRoot = null;
                            firstTrLogId = info.TrLogFileId;
                            firstTrLogOffset = info.TrLogOffset;
                            hasKeyIndex = true;
                            break;
                        }
                    }
                    finally
                    {
                        if (_nextRoot != null)
                        {
                            _nextRoot.Dispose();
                            _nextRoot = null;
                        }
                    }

                    // Corrupted kvi - could be removed
                    _fileCollection.MakeIdxUnknown(keyIndex.Key);
                }

                while (keyIndexes.Length > 0)
                {
                    var keyIndex = keyIndexes[^1];
                    keyIndexes = keyIndexes.Slice(0, keyIndexes.Length - 1);
                    if (keyIndex.Key != preserveKeyIndexKey)
                        _fileCollection.MakeIdxUnknown(keyIndex.Key);
                }

                LoadTransactionLogs(firstTrLogId, firstTrLogOffset, openUpToCommitUlong);
                if (!_readOnly)
                {
                    if (openUpToCommitUlong.HasValue || latestTrLogFileId != firstTrLogId && firstTrLogId != 0 ||
                        !hasKeyIndex && _fileCollection.FileInfos.Any(p => p.Value.SubDBId == 0))
                    {
                        // Need to create new trl if cannot append to last one so it is then written to kvi
                        if (openUpToCommitUlong.HasValue && _fileIdWithTransactionLog == 0)
                        {
                            WriteStartOfNewTransactionLogFile();
                            _fileWithTransactionLog!.HardFlush();
                            _fileWithTransactionLog.Truncate();
                            UpdateTransactionLogInBTreeRoot(_lastCommitted);
                        }

                        // When not opening history commit KVI file will be created by compaction
                        if (openUpToCommitUlong.HasValue)
                        {
                            CreateIndexFile(CancellationToken.None, preserveKeyIndexGeneration, true);
                        }
                    }

                    if (_fileIdWithTransactionLog != 0)
                    {
                        if (_writerWithTransactionLog == null)
                        {
                            _fileWithTransactionLog = FileCollection.GetFile(_fileIdWithTransactionLog);
                            _writerWithTransactionLog = _fileWithTransactionLog!.GetAppenderWriter();
                        }

                        if (_writerWithTransactionLog.GetCurrentPosition() > MaxTrLogFileSize)
                        {
                            WriteStartOfNewTransactionLogFile();
                        }
                    }

                    _fileCollection.DeleteAllUnknownFiles();
                }
                foreach (var fileInfo in _fileCollection.FileInfos)
                {
                    var ft = fileInfo.Value.FileType;
                    if (ft == KVFileType.TransactionLog || ft == KVFileType.PureValuesWithId ||
                        ft == KVFileType.PureValues)
                    {
                        _fileCollection.GetFile(fileInfo.Key)?.AdvisePrefetch();
                    }
                }
            }
            finally
            {
                if (_nextRoot != null)
                {
                    _nextRoot.Dispose();
                    _nextRoot = null;
                }
            }
        }

        bool IKeyValueDBInternal.LoadUsedFilesFromKeyIndex(uint fileId, IKeyIndex info)
        {
            return LoadUsedFilesFromKeyIndex(fileId, info);
        }

        public long CalculatePreserveKeyIndexGeneration(uint preserveKeyIndexKey)
        {
            if (preserveKeyIndexKey <= 0) return -1;
            return preserveKeyIndexKey < uint.MaxValue ? GetGeneration(preserveKeyIndexKey) : long.MaxValue;
        }

        internal uint CalculatePreserveKeyIndexKeyFromKeyIndexInfos(ReadOnlySpan<KeyIndexInfo> keyIndexes)
        {
            var preserveKeyIndexKey = uint.MaxValue;
            var preserveHistoryUpToCommitUlong = (ulong) Interlocked.Read(ref _preserveHistoryUpToCommitUlong);
            if (preserveHistoryUpToCommitUlong != ulong.MaxValue)
            {
                var nearKeyIndex = keyIndexes.Length - 1;
                while (nearKeyIndex >= 0)
                {
                    if (keyIndexes[nearKeyIndex].CommitUlong <= preserveHistoryUpToCommitUlong)
                    {
                        preserveKeyIndexKey = keyIndexes[nearKeyIndex].Key;
                        break;
                    }

                    nearKeyIndex--;
                }

                if (nearKeyIndex < 0)
                    preserveKeyIndexKey = 0;
            }

            return preserveKeyIndexKey;
        }

        long IKeyValueDBInternal.ReplaceBTreeValues(CancellationToken cancellation,
            Dictionary<ulong, ulong> newPositionMap)
        {
            return ReplaceBTreeValues(cancellation, newPositionMap);
        }

        void IKeyValueDBInternal.CreateIndexFile(CancellationToken cancellation, long preserveKeyIndexGeneration)
        {
            CreateIndexFile(cancellation, preserveKeyIndexGeneration);
        }

        AbstractBufferedWriter IKeyValueDBInternal.StartPureValuesFile(out uint fileId)
        {
            return StartPureValuesFile(out fileId);
        }

        internal void CreateIndexFile(CancellationToken cancellation, long preserveKeyIndexGeneration,
            bool fullSpeed = false)
        {
            var root = ReferenceAndGetLastCommitted();
            try
            {
                var idxFileId = CreateKeyIndexFile((IRootNode) root, cancellation, fullSpeed);
                MarkAsUnknown(_fileCollection.FileInfos.Where(p =>
                    p.Value.FileType == KVFileType.KeyIndex && p.Key != idxFileId &&
                    p.Value.Generation != preserveKeyIndexGeneration).Select(p => p.Key));
            }
            finally
            {
                DereferenceRootNodeInternal(root);
            }
        }

        internal bool LoadUsedFilesFromKeyIndex(uint fileId, IKeyIndex info)
        {
            try
            {
                var reader = FileCollection.GetFile(fileId)!.GetExclusiveReader();
                FileKeyIndex.SkipHeader(reader);
                var keyCount = info.KeyValueCount;
                var usedFileIds = new HashSet<uint>();
                if (info.Compression == KeyIndexCompression.Old)
                {
                    for (var i = 0; i < keyCount; i++)
                    {
                        var keyLength = reader.ReadVInt32();
                        reader.SkipBlock(keyLength);
                        var vFileId = reader.ReadVUInt32();
                        if (vFileId > 0) usedFileIds.Add(vFileId);
                        reader.SkipVUInt32();
                        reader.SkipVInt32();
                    }
                }
                else
                {
                    if (info.Compression != KeyIndexCompression.None)
                        return false;
                    for (var i = 0; i < keyCount; i++)
                    {
                        reader.SkipVUInt32();
                        var keyLengthWithoutPrefix = (int) reader.ReadVUInt32();
                        reader.SkipBlock(keyLengthWithoutPrefix);
                        var vFileId = reader.ReadVUInt32();
                        if (vFileId > 0) usedFileIds.Add(vFileId);
                        reader.SkipVUInt32();
                        reader.SkipVInt32();
                    }
                }

                var trlGeneration = GetGeneration(info.TrLogFileId);
                info.UsedFilesInOlderGenerations = usedFileIds.Select(GetGenerationIgnoreMissing)
                    .Where(gen => gen < trlGeneration).OrderBy(a => a).ToArray();

                if (reader.Eof) return true;
                if (reader.ReadInt32() == EndOfIndexFileMarker) return true;
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        bool LoadKeyIndex(uint fileId, IKeyIndex info)
        {
            try
            {
                var reader = FileCollection.GetFile(fileId)!.GetExclusiveReader();
                FileKeyIndex.SkipHeader(reader);
                var keyCount = info.KeyValueCount;
                _nextRoot!.TrLogFileId = info.TrLogFileId;
                _nextRoot.TrLogOffset = info.TrLogOffset;
                _nextRoot.CommitUlong = info.CommitUlong;
                if (info.Ulongs != null)
                    for (var i = 0u; i < info.Ulongs.Length; i++)
                    {
                        _nextRoot.SetUlong(i, info.Ulongs[i]);
                    }

                var usedFileIds = new HashSet<uint>();
                var cursor = _nextRoot.CreateCursor();
                if (info.Compression == KeyIndexCompression.Old)
                {
                    cursor.BuildTree(keyCount, (ref ByteBuffer key, Span<byte> trueValue) =>
                    {
                        var keyLength = reader.ReadVInt32();
                        key = ByteBuffer.NewAsync(new byte[Math.Abs(keyLength)]);
                        reader.ReadBlock(key);
                        if (keyLength < 0)
                        {
                            _compression.DecompressKey(ref key);
                        }

                        trueValue.Clear();
                        var vFileId = reader.ReadVUInt32();
                        if (vFileId > 0) usedFileIds.Add(vFileId);
                        MemoryMarshal.Write(trueValue, ref vFileId);
                        var valueOfs = reader.ReadVUInt32();
                        var valueSize = reader.ReadVInt32();
                        if (vFileId == 0)
                        {
                            var len = valueSize >> 24;
                            trueValue[4] = (byte) len;
                            switch (len)
                            {
                                case 7:
                                    trueValue[11] = (byte) (valueOfs >> 24);
                                    goto case 6;
                                case 6:
                                    trueValue[10] = (byte) (valueOfs >> 16);
                                    goto case 5;
                                case 5:
                                    trueValue[9] = (byte) (valueOfs >> 8);
                                    goto case 4;
                                case 4:
                                    trueValue[8] = (byte) valueOfs;
                                    goto case 3;
                                case 3:
                                    trueValue[7] = (byte) valueSize;
                                    goto case 2;
                                case 2:
                                    trueValue[6] = (byte) (valueSize >> 8);
                                    goto case 1;
                                case 1:
                                    trueValue[5] = (byte) (valueSize >> 16);
                                    break;
                                case 0:
                                    break;
                                default:
                                    throw new BTDBException("Corrupted DB");
                            }
                        }
                        else
                        {
                            MemoryMarshal.Write(trueValue.Slice(4), ref valueOfs);
                            MemoryMarshal.Write(trueValue.Slice(8), ref valueSize);
                        }
                    });
                }
                else
                {
                    if (info.Compression != KeyIndexCompression.None)
                        return false;
                    var prevKey = ByteBuffer.NewEmpty();
                    cursor.BuildTree(keyCount, (ref ByteBuffer key, Span<byte> trueValue) =>
                    {
                        var prefixLen = (int) reader.ReadVUInt32();
                        var keyLengthWithoutPrefix = (int) reader.ReadVUInt32();
                        var keyLen = prefixLen + keyLengthWithoutPrefix;
                        key.Expand(keyLen);
                        Array.Copy(prevKey.Buffer!, prevKey.Offset, key.Buffer!, key.Offset, prefixLen);
                        reader.ReadBlock(key.Slice(prefixLen));
                        prevKey = key;
                        var vFileId = reader.ReadVUInt32();
                        if (vFileId > 0) usedFileIds.Add(vFileId);
                        trueValue.Clear();
                        MemoryMarshal.Write(trueValue, ref vFileId);
                        var valueOfs = reader.ReadVUInt32();
                        var valueSize = reader.ReadVInt32();
                        if (vFileId == 0)
                        {
                            var len = valueSize >> 24;
                            trueValue[4] = (byte) len;
                            switch (len)
                            {
                                case 7:
                                    trueValue[11] = (byte) (valueOfs >> 24);
                                    goto case 6;
                                case 6:
                                    trueValue[10] = (byte) (valueOfs >> 16);
                                    goto case 5;
                                case 5:
                                    trueValue[9] = (byte) (valueOfs >> 8);
                                    goto case 4;
                                case 4:
                                    trueValue[8] = (byte) valueOfs;
                                    goto case 3;
                                case 3:
                                    trueValue[7] = (byte) valueSize;
                                    goto case 2;
                                case 2:
                                    trueValue[6] = (byte) (valueSize >> 8);
                                    goto case 1;
                                case 1:
                                    trueValue[5] = (byte) (valueSize >> 16);
                                    break;
                                case 0:
                                    break;
                                default:
                                    throw new BTDBException("Corrupted DB");
                            }
                        }
                        else
                        {
                            MemoryMarshal.Write(trueValue.Slice(4), ref valueOfs);
                            MemoryMarshal.Write(trueValue.Slice(8), ref valueSize);
                        }
                    });
                }

                var trlGeneration = GetGeneration(info.TrLogFileId);
                info.UsedFilesInOlderGenerations = usedFileIds.Select(GetGenerationIgnoreMissing)
                    .Where(gen => gen < trlGeneration).OrderBy(a => a).ToArray();

                if (reader.Eof) return true;
                if (reader.ReadInt32() == EndOfIndexFileMarker) return true;
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        void LoadTransactionLogs(uint firstTrLogId, uint firstTrLogOffset, ulong? openUpToCommitUlong)
        {
            while (firstTrLogId != 0 && firstTrLogId != uint.MaxValue)
            {
                _fileIdWithTransactionLog = 0;
                if (LoadTransactionLog(firstTrLogId, firstTrLogOffset, openUpToCommitUlong))
                {
                    _fileIdWithTransactionLog = firstTrLogId;
                }

                firstTrLogOffset = 0;
                _fileIdWithPreviousTransactionLog = firstTrLogId;
                var fileInfo = _fileCollection.FileInfoByIdx(firstTrLogId);
                if (fileInfo == null)
                    return;
                firstTrLogId = ((IFileTransactionLog) fileInfo).NextFileId;
            }
        }

        // Return true if it is suitable for continuing writing new transactions
        bool LoadTransactionLog(uint fileId, uint logOffset, ulong? openUpToCommitUlong)
        {
            if (openUpToCommitUlong.HasValue && _lastCommitted.CommitUlong >= openUpToCommitUlong)
            {
                return false;
            }

            Span<byte> trueValue = stackalloc byte[12];
            var collectionFile = FileCollection.GetFile(fileId);
            var reader = collectionFile!.GetExclusiveReader();
            try
            {
                if (logOffset == 0)
                {
                    FileTransactionLog.SkipHeader(reader);
                }
                else
                {
                    reader.SkipBlock(logOffset);
                }

                if (reader.Eof) return true;
                var afterTemporaryEnd = false;
                var finishReading = false;
                ICursor cursor;
                ICursor cursor2;
                if (_nextRoot != null)
                {
                    cursor = _nextRoot.CreateCursor();
                    cursor2 = _nextRoot.CreateCursor();
                }
                else
                {
                    cursor = _lastCommitted.CreateCursor();
                    cursor2 = _lastCommitted.CreateCursor();
                }


                while (!reader.Eof)
                {
                    var command = (KVCommandType) reader.ReadUInt8();
                    if (command == 0 && afterTemporaryEnd)
                    {
                        collectionFile.SetSize(reader.GetCurrentPosition() - 1);
                        return true;
                    }

                    if (finishReading)
                    {
                        return false;
                    }

                    afterTemporaryEnd = false;
                    switch (command & KVCommandType.CommandMask)
                    {
                        case KVCommandType.CreateOrUpdateDeprecated:
                        case KVCommandType.CreateOrUpdate:
                        {
                            if (_nextRoot == null) return false;
                            var keyLen = reader.ReadVInt32();
                            var valueLen = reader.ReadVInt32();
                            var key = new byte[keyLen];
                            reader.ReadBlock(key);
                            var keyBuf = ByteBuffer.NewAsync(key);
                            if ((command & KVCommandType.FirstParamCompressed) != 0)
                            {
                                _compression.DecompressKey(ref keyBuf);
                            }

                            trueValue.Clear();
                            var valueOfs = (uint) reader.GetCurrentPosition();
                            var valueSize = (command & KVCommandType.SecondParamCompressed) != 0 ? -valueLen : valueLen;
                            if (valueLen <= MaxValueSizeInlineInMemory &&
                                (command & KVCommandType.SecondParamCompressed) == 0)
                            {
                                var value = 0;
                                MemoryMarshal.Write(trueValue, ref value);
                                trueValue[4] = (byte) valueLen;
                                reader.ReadBlock(trueValue.Slice(5, valueLen));
                            }
                            else
                            {
                                MemoryMarshal.Write(trueValue, ref fileId);
                                MemoryMarshal.Write(trueValue.Slice(4), ref valueOfs);
                                MemoryMarshal.Write(trueValue.Slice(8), ref valueSize);
                                reader.SkipBlock(valueLen);
                            }

                            cursor.Upsert(keyBuf.AsSyncReadOnlySpan(), trueValue);
                        }
                            break;
                        case KVCommandType.EraseOne:
                        {
                            if (_nextRoot == null) return false;
                            var keyLen = reader.ReadVInt32();
                            var key = new byte[keyLen];
                            reader.ReadBlock(key);
                            var keyBuf = ByteBuffer.NewAsync(key);
                            if ((command & KVCommandType.FirstParamCompressed) != 0)
                            {
                                _compression.DecompressKey(ref keyBuf);
                            }

                            if (cursor.FindExact(keyBuf.AsSyncReadOnlySpan()))
                                cursor.Erase();
                        }
                            break;
                        case KVCommandType.EraseRange:
                        {
                            if (_nextRoot == null) return false;
                            var keyLen1 = reader.ReadVInt32();
                            var keyLen2 = reader.ReadVInt32();
                            var key = new byte[keyLen1];
                            reader.ReadBlock(key);
                            var keyBuf = ByteBuffer.NewAsync(key);
                            if ((command & KVCommandType.FirstParamCompressed) != 0)
                            {
                                _compression.DecompressKey(ref keyBuf);
                            }

                            var findResult = cursor.Find(keyBuf.AsSyncReadOnlySpan());
                            if (findResult == FindResult.Previous) cursor.MoveNext();
                            key = new byte[keyLen2];
                            reader.ReadBlock(key);
                            keyBuf = ByteBuffer.NewAsync(key);
                            if ((command & KVCommandType.SecondParamCompressed) != 0)
                            {
                                _compression.DecompressKey(ref keyBuf);
                            }

                            findResult = cursor2.Find(keyBuf.AsSyncReadOnlySpan());
                            if (findResult == FindResult.Next) cursor2.MovePrevious();
                            cursor.EraseTo(cursor2);
                        }
                            break;
                        case KVCommandType.DeltaUlongs:
                        {
                            if (_nextRoot == null) return false;
                            var idx = reader.ReadVUInt32();
                            var delta = reader.ReadVUInt64();
                            // overflow is expected in case Ulong is decreasing but that should be rare
                            _nextRoot.SetUlong(idx, unchecked(_nextRoot.GetUlong(idx) + delta));
                        }
                            break;
                        case KVCommandType.TransactionStart:
                            if (!reader.CheckMagic(MagicStartOfTransaction))
                                return false;
                            if (_nextRoot != null)
                            {
                                _nextRoot.Dispose();
                                _nextRoot = null;
                                return false;
                            }

                            _nextRoot = _lastCommitted.CreateWritableTransaction();
                            cursor.SetNewRoot(_nextRoot);
                            cursor2.SetNewRoot(_nextRoot);
                            break;
                        case KVCommandType.CommitWithDeltaUlong:
                            if (_nextRoot == null) return false;
                            unchecked // overflow is expected in case commitUlong is decreasing but that should be rare
                            {
                                _nextRoot.CommitUlong += reader.ReadVUInt64();
                            }

                            goto case KVCommandType.Commit;
                        case KVCommandType.Commit:
                            if (_nextRoot == null) return false;
                            _nextRoot.TrLogFileId = fileId;
                            _nextRoot.TrLogOffset = (uint) reader.GetCurrentPosition();
                            _lastCommitted.Dispose();
                            _nextRoot.Commit();
                            _lastCommitted = _nextRoot;
                            _nextRoot = null;
                            if (openUpToCommitUlong.HasValue && _lastCommitted.CommitUlong >= openUpToCommitUlong)
                            {
                                finishReading = true;
                            }

                            break;
                        case KVCommandType.Rollback:
                            _nextRoot.Dispose();
                            _nextRoot = null;
                            break;
                        case KVCommandType.EndOfFile:
                            return false;
                        case KVCommandType.TemporaryEndOfFile:
                            _lastCommitted.TrLogFileId = fileId;
                            _lastCommitted.TrLogOffset = (uint) reader.GetCurrentPosition();
                            afterTemporaryEnd = true;
                            break;
                        default:
                            if (_nextRoot != null)
                            {
                                _nextRoot.Dispose();
                                _nextRoot = null;
                            }

                            return false;
                    }
                }

                return afterTemporaryEnd;
            }
            catch (EndOfStreamException)
            {
                if (_nextRoot != null)
                {
                    _nextRoot.Dispose();
                    _nextRoot = null;
                }

                return false;
            }
        }

        uint LinkTransactionLogFileIds(uint latestTrLogFileId)
        {
            var nextId = 0u;
            var currentId = latestTrLogFileId;
            while (currentId != 0)
            {
                var fileInfo = _fileCollection.FileInfoByIdx(currentId);

                var fileTransactionLog = fileInfo as IFileTransactionLog;
                if (fileTransactionLog == null) break;
                fileTransactionLog.NextFileId = nextId;
                nextId = currentId;
                currentId = fileTransactionLog.PreviousFileId;
            }

            return nextId;
        }

        public void Dispose()
        {
            _compactorScheduler?.RemoveCompactAction(_compactFunc!);
            lock (_writeLock)
            {
                if (_writingTransaction != null)
                    throw new BTDBException("Cannot dispose KeyValueDB when writing transaction still running");
                while (_writeWaitingQueue.Count > 0)
                {
                    _writeWaitingQueue.Dequeue().TrySetCanceled();
                }

                DereferenceRoot(_lastCommitted);
                FreeWaitingToDisposeUnsafe();
            }

            if (_writerWithTransactionLog != null)
            {
                _writerWithTransactionLog.WriteUInt8((byte) KVCommandType.TemporaryEndOfFile);
                _fileWithTransactionLog!.HardFlushTruncateSwitchToDisposedMode();
            }
        }

        public bool DurableTransactions { get; set; }

        public IRootNodeInternal ReferenceAndGetLastCommitted()
        {
            while (true)
            {
                var node = _lastCommitted;
                // Memory barrier inside next statement
                if (!node.Reference())
                {
                    return node;
                }
            }
        }

        void IKeyValueDBInternal.MarkAsUnknown(IEnumerable<uint> fileIds)
        {
            MarkAsUnknown(fileIds);
        }

        IFileCollectionWithFileInfos IKeyValueDBInternal.FileCollection => FileCollection;

        bool IKeyValueDBInternal.ContainsValuesAndDoesNotTouchGeneration(uint fileKey, long dontTouchGeneration)
        {
            return ContainsValuesAndDoesNotTouchGeneration(fileKey, dontTouchGeneration);
        }

        public IFileCollectionWithFileInfos FileCollection => _fileCollection;

        bool IKeyValueDBInternal.AreAllTransactionsBeforeFinished(long transactionId)
        {
            return AreAllTransactionsBeforeFinished(transactionId);
        }

        public IRootNodeInternal ReferenceAndGetOldestRoot()
        {
            while (true)
            {
                var oldestRoot = _lastCommitted;
                foreach (var usedTransaction in _usedNodesInReadonlyTransactions.Keys)
                {
                    if (usedTransaction.ShouldBeDisposed)
                        continue;
                    if (unchecked(usedTransaction.TransactionId - oldestRoot.TransactionId) < 0)
                    {
                        oldestRoot = usedTransaction;
                    }
                }

                // Memory barrier inside next statement
                if (!oldestRoot.Reference())
                {
                    return oldestRoot;
                }
            }
        }

        public IKeyValueDBTransaction StartTransaction()
        {
            while (true)
            {
                var node = _lastCommitted;
                // Memory barrier inside next statement
                if (!node.Reference())
                {
                    _usedNodesInReadonlyTransactions.TryAdd(node, true);
                    return new BTreeKeyValueDBTransaction(this, node, false, false);
                }
            }
        }

        public IKeyValueDBTransaction StartReadOnlyTransaction()
        {
            while (true)
            {
                var node = _lastCommitted;
                // Memory barrier inside next statement
                if (!node.Reference())
                {
                    _usedNodesInReadonlyTransactions.TryAdd(node, true);
                    return new BTreeKeyValueDBTransaction(this, node, false, true);
                }
            }
        }

        public ValueTask<IKeyValueDBTransaction> StartWritingTransaction()
        {
            lock (_writeLock)
            {
                if (_writingTransaction == null)
                {
                    return new ValueTask<IKeyValueDBTransaction>(NewWritingTransactionUnsafe());
                }

                var tcs = new TaskCompletionSource<IKeyValueDBTransaction>();
                _writeWaitingQueue.Enqueue(tcs);
                return new ValueTask<IKeyValueDBTransaction>(tcs.Task);
            }
        }

        class FreqStats<K> : RefDictionary<K, uint> where K : IEquatable<K>
        {
            public void Inc(K key)
            {
                GetOrAddValueRef(key)++;
            }

            public void AddToStringBuilder(StringBuilder sb, string name)
            {
                sb.Append(name);
                sb.Append(" => Count\n");
                var list = new KeyValuePair<K, uint>[Count];
                CopyTo(list, 0);
                Array.Sort(list,
                    Comparer<KeyValuePair<K, uint>>.Create((a, b) =>
                        Comparer<K>.Default.Compare(a.Key, b.Key)));
                foreach (var t in list)
                {
                    sb.AppendFormat("{0} => {1}\n", t.Key, t.Value);
                }
            }
        }

        public string CalcStats()
        {
            var oldestRoot = (IRootNode)ReferenceAndGetOldestRoot();
            var lastCommitted = (IRootNode)ReferenceAndGetLastCommitted();
            try
            {
                var sb = new StringBuilder(
                    $"KeyValueCount:{lastCommitted.GetCount()}\nFileCount:{FileCollection.GetCount()}\nFileGeneration:{FileCollection.LastFileGeneration}\n");
                sb.Append($"LastTrId:{lastCommitted.TransactionId},TRL:{lastCommitted.TrLogFileId},ComUlong:{lastCommitted.CommitUlong}\n");
                sb.Append($"OldestTrId:{oldestRoot.TransactionId},TRL:{oldestRoot.TrLogFileId},ComUlong:{oldestRoot.CommitUlong}\n");
                foreach (var file in _fileCollection.FileInfos)
                {
                    sb.AppendFormat("{0} Size:{1} Type:{2} Gen:{3}\n", file.Key, FileCollection.GetSize(file.Key),
                        file.Value.FileType, file.Value.Generation);
                }

                return sb.ToString();
            }
            finally
            {
                DereferenceRootNodeInternal(oldestRoot);
                DereferenceRootNodeInternal(lastCommitted);
            }
        }

        public bool Compact(CancellationToken cancellation)
        {
            return new Compactor(this, cancellation).Run();
        }

        public void CreateKvi(CancellationToken cancellation)
        {
            CreateIndexFile(cancellation, 0);
        }

        public IKeyValueDBLogger? Logger { get; set; }

        public uint CompactorRamLimitInMb { get; set; }

        public ulong? PreserveHistoryUpToCommitUlong
        {
            get
            {
                var preserveHistoryUpToCommitUlong = (ulong) Interlocked.Read(ref _preserveHistoryUpToCommitUlong);
                return preserveHistoryUpToCommitUlong == ulong.MaxValue
                    ? null
                    : (ulong?) preserveHistoryUpToCommitUlong;
            }
            set => Interlocked.Exchange(ref _preserveHistoryUpToCommitUlong, (long) (value ?? ulong.MaxValue));
        }

        internal IRootNode MakeWritableTransaction(BTreeKeyValueDBTransaction keyValueDBTransaction,
            IRootNode btreeRoot)
        {
            lock (_writeLock)
            {
                if (_writingTransaction != null)
                    throw new BTDBTransactionRetryException("Another writing transaction already running");
                if (_lastCommitted != btreeRoot)
                    throw new BTDBTransactionRetryException("Another writing transaction already finished");
                _writingTransaction = keyValueDBTransaction;
                var result = _lastCommitted.CreateWritableTransaction();
                DereferenceRoot(btreeRoot);
                return result;
            }
        }

        internal void CommitFromCompactor(IRootNode root)
        {
            lock (_writeLock)
            {
                _writingTransaction = null;
                if (_lastCommitted.Dereference())
                {
                    _lastCommitted.Dispose();
                }

                _lastCommitted = root;
                _lastCommitted.Commit();
                TryDequeWaiterForWritingTransaction();
            }
        }

        internal void CommitWritingTransaction(IRootNode root, bool temporaryCloseTransactionLog)
        {
            try
            {
                WriteUlongsDiff(root, _lastCommitted);
                var deltaUlong = unchecked(root.CommitUlong - _lastCommitted.CommitUlong);
                if (deltaUlong != 0)
                {
                    _writerWithTransactionLog!.WriteUInt8((byte) KVCommandType.CommitWithDeltaUlong);
                    _writerWithTransactionLog.WriteVUInt64(deltaUlong);
                }
                else
                {
                    _writerWithTransactionLog!.WriteUInt8((byte) KVCommandType.Commit);
                }

                if (DurableTransactions)
                {
                    _fileWithTransactionLog!.HardFlush();
                }
                else
                {
                    _fileWithTransactionLog!.Flush();
                }

                UpdateTransactionLogInBTreeRoot(root);
                if (temporaryCloseTransactionLog)
                {
                    _writerWithTransactionLog!.WriteUInt8((byte) KVCommandType.TemporaryEndOfFile);
                    _fileWithTransactionLog!.Flush();
                    _fileWithTransactionLog.Truncate();
                }

                lock (_writeLock)
                {
                    _writingTransaction = null;
                    if (_lastCommitted.Dereference())
                    {
                        _lastCommitted.Dispose();
                    }

                    _lastCommitted = root;
                    root = null;
                    _lastCommitted.Commit();
                    TryDequeWaiterForWritingTransaction();
                }
            }
            finally
            {
                root?.Dispose();
            }
        }

        void WriteUlongsDiff(IRootNode newArray, IRootNode oldArray)
        {
            var newCount = newArray.GetUlongCount();
            var oldCount = oldArray.GetUlongCount();
            var maxCount = Math.Max(newCount, oldCount);
            for (var i = 0u; i < maxCount; i++)
            {
                var oldValue = i < oldCount ? oldArray.GetUlong(i) : 0;
                var newValue = i < newCount ? newArray.GetUlong(i) : 0;
                var deltaUlong = unchecked(newValue - oldValue);
                if (deltaUlong != 0)
                {
                    _writerWithTransactionLog!.WriteUInt8((byte) KVCommandType.DeltaUlongs);
                    _writerWithTransactionLog.WriteVUInt32(i);
                    _writerWithTransactionLog.WriteVUInt64(deltaUlong);
                }
            }
        }

        void UpdateTransactionLogInBTreeRoot(IRootNode root)
        {
            if (root.TrLogFileId != _fileIdWithTransactionLog && root.TrLogFileId != 0)
            {
                _compactorScheduler?.AdviceRunning(false);
            }

            root.TrLogFileId = _fileIdWithTransactionLog;
            if (_writerWithTransactionLog != null)
            {
                root.TrLogOffset = (uint) _writerWithTransactionLog.GetCurrentPosition();
            }
            else
            {
                root.TrLogOffset = 0;
            }
        }

        void TryDequeWaiterForWritingTransaction()
        {
            FreeWaitingToDisposeUnsafe();
            if (_writeWaitingQueue.Count == 0) return;
            var tcs = _writeWaitingQueue.Dequeue();
            tcs.SetResult(NewWritingTransactionUnsafe());
        }

        void TryFreeWaitingToDispose()
        {
            var taken = false;
            Monitor.TryEnter(_writeLock, ref taken);
            try
            {
                if (taken)
                {
                    FreeWaitingToDisposeUnsafe();
                }
            }
            finally
            {
                if (taken)
                    Monitor.Exit(_writeLock);
            }
        }

        BTreeKeyValueDBTransaction NewWritingTransactionUnsafe()
        {
            if (_readOnly) throw new BTDBException("Database opened in readonly mode");
            FreeWaitingToDisposeUnsafe();
            var newTransactionRoot = _lastCommitted.CreateWritableTransaction();
            try
            {
                var tr = new BTreeKeyValueDBTransaction(this, newTransactionRoot, true, false);
                _writingTransaction = tr;
                return tr;
            }
            catch
            {
                newTransactionRoot.Dispose();
                throw;
            }
        }

        void FreeWaitingToDisposeUnsafe()
        {
            while (_waitingToDispose.TryTake(out var node))
            {
                node.Dispose();
            }
        }

        internal void RevertWritingTransaction(IRootNode writtenToTransactionLog, bool nothingWrittenToTransactionLog)
        {
            writtenToTransactionLog.Dispose();
            if (!nothingWrittenToTransactionLog)
            {
                _writerWithTransactionLog!.WriteUInt8((byte) KVCommandType.Rollback);
                _writerWithTransactionLog.FlushBuffer();
                _fileWithTransactionLog!.Flush();
                lock (_writeLock)
                {
                    _writingTransaction = null;
                    UpdateTransactionLogInBTreeRoot(_lastCommitted);
                    TryDequeWaiterForWritingTransaction();
                }
            }
            else
            {
                lock (_writeLock)
                {
                    _writingTransaction = null;
                    TryDequeWaiterForWritingTransaction();
                }
            }
        }

        internal void WriteStartTransaction()
        {
            if (_fileIdWithTransactionLog == 0)
            {
                WriteStartOfNewTransactionLogFile();
            }
            else
            {
                if (_writerWithTransactionLog == null)
                {
                    _fileWithTransactionLog = FileCollection.GetFile(_fileIdWithTransactionLog);
                    _writerWithTransactionLog = _fileWithTransactionLog!.GetAppenderWriter();
                }

                if (_writerWithTransactionLog.GetCurrentPosition() > MaxTrLogFileSize)
                {
                    WriteStartOfNewTransactionLogFile();
                }
            }

            _writerWithTransactionLog!.WriteUInt8((byte) KVCommandType.TransactionStart);
            _writerWithTransactionLog.WriteByteArrayRaw(MagicStartOfTransaction);
        }

        void WriteStartOfNewTransactionLogFile()
        {
            if (_writerWithTransactionLog != null)
            {
                _writerWithTransactionLog.WriteUInt8((byte) KVCommandType.EndOfFile);
                _fileWithTransactionLog!.HardFlushTruncateSwitchToReadOnlyMode();
                _fileIdWithPreviousTransactionLog = _fileIdWithTransactionLog;
            }

            _fileWithTransactionLog = FileCollection.AddFile("trl");
            _fileIdWithTransactionLog = _fileWithTransactionLog.Index;
            var transactionLog = new FileTransactionLog(FileCollection.NextGeneration(), FileCollection.Guid,
                _fileIdWithPreviousTransactionLog);
            _writerWithTransactionLog = _fileWithTransactionLog.GetAppenderWriter();
            transactionLog.WriteHeader(_writerWithTransactionLog);
            FileCollection.SetInfo(_fileIdWithTransactionLog, transactionLog);
        }

        public void WriteCreateOrUpdateCommand(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value,
            Span<byte> trueValue)
        {
            var command = KVCommandType.CreateOrUpdate;
            if (_compression.ShouldTryToCompressKey(key.Length))
            {
                if (_compression.CompressKey(ref key))
                {
                    command |= KVCommandType.FirstParamCompressed;
                }
            }

            var valueSize = value.Length;
            if (_compression.CompressValue(ref value))
            {
                command |= KVCommandType.SecondParamCompressed;
                valueSize = -value.Length;
            }

            var trlPos = _writerWithTransactionLog!.GetCurrentPosition();
            if (trlPos > 256 && trlPos + key.Length + 16 + value.Length > MaxTrLogFileSize)
            {
                WriteStartOfNewTransactionLogFile();
            }

            _writerWithTransactionLog!.WriteUInt8((byte) command);
            _writerWithTransactionLog.WriteVInt32(key.Length);
            _writerWithTransactionLog.WriteVInt32(value.Length);
            _writerWithTransactionLog.WriteBlock(key);
            if (valueSize != 0)
            {
                if (valueSize > 0 && valueSize <= MaxValueSizeInlineInMemory)
                {
                    var zero = 0;
                    MemoryMarshal.Write(trueValue, ref zero);
                    trueValue[4] = (byte) value.Length;
                    value.CopyTo(trueValue.Slice(5));
                }
                else
                {
                    MemoryMarshal.Write(trueValue, ref _fileIdWithTransactionLog);
                    var valueOfs = (uint) _writerWithTransactionLog.GetCurrentPosition();
                    MemoryMarshal.Write(trueValue.Slice(4), ref valueOfs);
                    MemoryMarshal.Write(trueValue.Slice(8), ref valueSize);
                }

                _writerWithTransactionLog.WriteBlock(value);
            }
            else
            {
                trueValue.Clear();
            }
        }

        public uint CalcValueSize(uint valueFileId, uint valueOfs, int valueSize)
        {
            if (valueFileId == 0)
            {
                return valueOfs & 0xff;
            }

            return (uint) Math.Abs(valueSize);
        }

        public ReadOnlySpan<byte> ReadValue(ReadOnlySpan<byte> trueValue)
        {
            var valueFileId = MemoryMarshal.Read<uint>(trueValue);
            if (valueFileId == 0)
            {
                var len = trueValue[4];
                return trueValue.Slice(5, len);
            }

            var valueSize = MemoryMarshal.Read<int>(trueValue.Slice(8));
            if (valueSize == 0) return new ReadOnlySpan<byte>();
            var valueOfs = MemoryMarshal.Read<uint>(trueValue.Slice(4));

            var compressed = false;
            if (valueSize < 0)
            {
                compressed = true;
                valueSize = -valueSize;
            }

            Span<byte> result = new byte[valueSize];
            var file = FileCollection.GetFile(valueFileId);
            if (file == null)
                throw new BTDBException(
                    $"ReadValue({valueFileId},{valueOfs},{valueSize}) compressed: {compressed} file does not exist in {CalcStats()}");
            file.RandomRead(result, valueOfs, false);
            if (compressed)
                result = _compression.DecompressValue(result);
            return result;
        }

        public void WriteEraseOneCommand(ByteBuffer key)
        {
            var command = KVCommandType.EraseOne;
            if (_compression.ShouldTryToCompressKey(key.Length))
            {
                if (_compression.CompressKey(ref key))
                {
                    command |= KVCommandType.FirstParamCompressed;
                }
            }

            if (_writerWithTransactionLog!.GetCurrentPosition() > MaxTrLogFileSize)
            {
                WriteStartOfNewTransactionLogFile();
            }

            _writerWithTransactionLog!.WriteUInt8((byte) command);
            _writerWithTransactionLog.WriteVInt32(key.Length);
            _writerWithTransactionLog.WriteBlock(key);
        }

        public void WriteEraseRangeCommand(ByteBuffer firstKey, ByteBuffer secondKey)
        {
            var command = KVCommandType.EraseRange;
            if (_compression.ShouldTryToCompressKey(firstKey.Length))
            {
                if (_compression.CompressKey(ref firstKey))
                {
                    command |= KVCommandType.FirstParamCompressed;
                }
            }

            if (_compression.ShouldTryToCompressKey(secondKey.Length))
            {
                if (_compression.CompressKey(ref secondKey))
                {
                    command |= KVCommandType.SecondParamCompressed;
                }
            }

            if (_writerWithTransactionLog!.GetCurrentPosition() > MaxTrLogFileSize)
            {
                WriteStartOfNewTransactionLogFile();
            }

            _writerWithTransactionLog!.WriteUInt8((byte) command);
            _writerWithTransactionLog.WriteVInt32(firstKey.Length);
            _writerWithTransactionLog.WriteVInt32(secondKey.Length);
            _writerWithTransactionLog.WriteBlock(firstKey);
            _writerWithTransactionLog.WriteBlock(secondKey);
        }

        uint CreateKeyIndexFile(IRootNode root, CancellationToken cancellation, bool fullSpeed)
        {
            var bytesPerSecondLimiter = new BytesPerSecondLimiter(fullSpeed ? 0 : CompactorWriteBytesPerSecondLimit);
            var file = FileCollection.AddFile("kvi");
            var writer = file.GetAppenderWriter();
            var keyCount = root.GetCount();
            if (root.TrLogFileId != 0)
                FileCollection.ConcurentTemporaryTruncate(root.TrLogFileId, root.TrLogOffset);
            var keyIndex = new FileKeyIndex(FileCollection.NextGeneration(), FileCollection.Guid, root.TrLogFileId,
                root.TrLogOffset, keyCount, root.CommitUlong, KeyIndexCompression.None, root.UlongsArray);
            keyIndex.WriteHeader(writer);
            var usedFileIds = new HashSet<uint>();
            if (keyCount > 0)
            {
                var keyValueIterateCtx = new KeyValueIterateCtx {CancellationToken = cancellation};
                root.KeyValueIterate(ref keyValueIterateCtx, (ref KeyValueIterateCtx ctx) =>
                {
                    var memberValue = ctx.CurrentValue;
                    writer.WriteVUInt32(ctx.PreviousCurrentCommonLength);
                    writer.WriteVUInt32((uint) (ctx.CurrentPrefix.Length+ctx.CurrentSuffix.Length-ctx.PreviousCurrentCommonLength));
                    if (ctx.CurrentPrefix.Length <= ctx.PreviousCurrentCommonLength)
                    {
                        writer.WriteBlock(ctx.CurrentSuffix.Slice((int)ctx.PreviousCurrentCommonLength-ctx.CurrentPrefix.Length));
                    }
                    else
                    {
                        writer.WriteBlock(ctx.CurrentPrefix.Slice((int)ctx.PreviousCurrentCommonLength));
                        writer.WriteBlock(ctx.CurrentSuffix);
                    }
                    var vFileId = MemoryMarshal.Read<uint>(memberValue);
                    if (vFileId > 0) usedFileIds.Add(vFileId);
                    writer.WriteVUInt32(vFileId);
                    if (vFileId == 0)
                    {
                        uint valueOfs;
                        int valueSize;
                        var inlineValueBuf = memberValue.Slice(5);
                        var valueLen = memberValue[4];
                        switch (valueLen)
                        {
                            case 0:
                                valueOfs = 0;
                                valueSize = 0;
                                break;
                            case 1:
                                valueOfs = 0;
                                valueSize = 0x1000000 | (inlineValueBuf[0] << 16);
                                break;
                            case 2:
                                valueOfs = 0;
                                valueSize = 0x2000000 | (inlineValueBuf[0] << 16) | (inlineValueBuf[1] << 8);
                                break;
                            case 3:
                                valueOfs = 0;
                                valueSize = 0x3000000 | (inlineValueBuf[0] << 16) | (inlineValueBuf[1] << 8) |
                                            inlineValueBuf[2];
                                break;
                            case 4:
                                valueOfs = inlineValueBuf[3];
                                valueSize = 0x4000000 | (inlineValueBuf[0] << 16) | (inlineValueBuf[1] << 8) |
                                            inlineValueBuf[2];
                                break;
                            case 5:
                                valueOfs = inlineValueBuf[3] | ((uint) inlineValueBuf[4] << 8);
                                valueSize = 0x5000000 | (inlineValueBuf[0] << 16) | (inlineValueBuf[1] << 8) |
                                            inlineValueBuf[2];
                                break;
                            case 6:
                                valueOfs = inlineValueBuf[3] | ((uint) inlineValueBuf[4] << 8) |
                                           ((uint) inlineValueBuf[5] << 16);
                                valueSize = 0x6000000 | (inlineValueBuf[0] << 16) | (inlineValueBuf[1] << 8) |
                                            inlineValueBuf[2];
                                break;
                            case 7:
                                valueOfs = inlineValueBuf[3] | ((uint) inlineValueBuf[4] << 8) |
                                           ((uint) inlineValueBuf[5] << 16) | ((uint) inlineValueBuf[6] << 24);
                                valueSize = 0x7000000 | (inlineValueBuf[0] << 16) | (inlineValueBuf[1] << 8) |
                                            inlineValueBuf[2];
                                break;
                            default:
                                throw new ArgumentOutOfRangeException();
                        }

                        writer.WriteVUInt32(valueOfs);
                        writer.WriteVInt32(valueSize);
                    }
                    else
                    {
                        var valueOfs = MemoryMarshal.Read<uint>(memberValue.Slice(4));
                        var valueSize = MemoryMarshal.Read<int>(memberValue.Slice(8));
                        writer.WriteVUInt32(valueOfs);
                        writer.WriteVInt32(valueSize);
                    }
                    bytesPerSecondLimiter.Limit((ulong) writer.GetCurrentPosition());
                });
            }

            writer.FlushBuffer();
            file.HardFlush();
            writer.WriteInt32(EndOfIndexFileMarker);
            writer.FlushBuffer();
            file.HardFlush();
            file.Truncate();
            var trlGeneration = GetGeneration(keyIndex.TrLogFileId);
            keyIndex.UsedFilesInOlderGenerations = usedFileIds.Select(GetGenerationIgnoreMissing)
                .Where(gen => gen < trlGeneration).OrderBy(a => a).ToArray();
            FileCollection.SetInfo(file.Index, keyIndex);
            Logger?.KeyValueIndexCreated(file.Index, keyIndex.KeyValueCount, file.GetSize(),
                TimeSpan.FromMilliseconds(bytesPerSecondLimiter.TotalTimeInMs));
            return file.Index;
        }

        internal bool ContainsValuesAndDoesNotTouchGeneration(uint fileId, long dontTouchGeneration)
        {
            var info = FileCollection.FileInfoByIdx(fileId);
            if (info == null) return false;
            if (info.Generation >= dontTouchGeneration) return false;
            return info.FileType == KVFileType.TransactionLog || info.FileType == KVFileType.PureValues;
        }

        internal AbstractBufferedWriter StartPureValuesFile(out uint fileId)
        {
            var fId = FileCollection.AddFile("pvl");
            fileId = fId.Index;
            var pureValues = new FilePureValues(FileCollection.NextGeneration(), FileCollection.Guid);
            var writer = fId.GetAppenderWriter();
            FileCollection.SetInfo(fId.Index, pureValues);
            pureValues.WriteHeader(writer);
            return writer;
        }

        internal long ReplaceBTreeValues(CancellationToken cancellation, Dictionary<ulong, ulong> newPositionMap)
        {
            byte[] restartKey = null;
            while (true)
            {
                var iterationTimeOut = DateTime.UtcNow + TimeSpan.FromMilliseconds(50);
                using (var tr = StartWritingTransaction().Result)
                {
                    var newRoot = ((BTreeKeyValueDBTransaction) tr).BTreeRoot;
                    var cursor = newRoot.CreateCursor();
                    if (restartKey != null)
                    {
                        cursor.Find(restartKey);
                        cursor.MovePrevious();
                    }
                    else
                    {
                        cursor.MoveNext();
                    }

                    var ctx = default(ValueReplacerCtx);
                    ctx._operationTimeout = iterationTimeOut;
                    ctx._interrupted = false;
                    ctx._positionMap = newPositionMap;
                    ctx._cancellation = cancellation;
                    cursor.ValueReplacer(ref ctx);
                    restartKey = ctx._interruptedKey;

                    cancellation.ThrowIfCancellationRequested();
                    ((BTreeKeyValueDBTransaction) tr).CommitFromCompactor();
                    if (!ctx._interrupted)
                    {
                        return newRoot.TransactionId;
                    }
                }

                Thread.Sleep(10);
            }
        }

        long IKeyValueDBInternal.GetGeneration(uint fileId)
        {
            return GetGeneration(fileId);
        }

        internal void MarkAsUnknown(IEnumerable<uint> fileIds)
        {
            foreach (var fileId in fileIds)
            {
                _fileCollection.MakeIdxUnknown(fileId);
            }
        }

        internal long GetGeneration(uint fileId)
        {
            if (fileId == 0) return -1;
            var fileInfo = FileCollection.FileInfoByIdx(fileId);
            if (fileInfo == null)
            {
                throw new ArgumentOutOfRangeException(nameof(fileId));
            }

            return fileInfo.Generation;
        }

        internal long GetGenerationIgnoreMissing(uint fileId)
        {
            if (fileId == 0) return -1;
            var fileInfo = FileCollection.FileInfoByIdx(fileId);
            if (fileInfo == null)
            {
                return -1;
            }

            return fileInfo.Generation;
        }

        internal bool AreAllTransactionsBeforeFinished(long transactionId)
        {
            foreach (var usedTransaction in _usedNodesInReadonlyTransactions.Keys)
            {
                if (usedTransaction.ShouldBeDisposed) continue;
                if (usedTransaction.TransactionId - transactionId >= 0) continue;
                return false;
            }

            return true;
        }

        internal ulong DistanceFromLastKeyIndex(IRootNode root)
        {
            var keyIndex = FileCollection.FileInfos.Where(p => p.Value.FileType == KVFileType.KeyIndex)
                .Select(p => (IKeyIndex) p.Value).FirstOrDefault();
            if (keyIndex == null)
            {
                if (FileCollection.FileInfos.Count(p => p.Value.SubDBId == 0) > 1) return ulong.MaxValue;
                return root.TrLogOffset;
            }

            if (root.TrLogFileId != keyIndex.TrLogFileId) return ulong.MaxValue;
            return root.TrLogOffset - keyIndex.TrLogOffset;
        }

        public T? GetSubDB<T>(long id) where T : class
        {
            if (_subDBs.TryGetValue(id, out var subDB))
            {
                if (!(subDB is T)) throw new ArgumentException($"SubDB of id {id} is not type {typeof(T).FullName}");
                return (T) subDB;
            }

            if (typeof(T) == typeof(IChunkStorage))
            {
                subDB = new ChunkStorageInKV(id, _fileCollection, MaxTrLogFileSize);
            }

            _subDBs.Add(id, subDB);
            return (T) subDB;
        }

        public void DereferenceRoot(IRootNode currentRoot)
        {
            if (currentRoot.Dereference())
            {
                _usedNodesInReadonlyTransactions.TryRemove(currentRoot);
                _waitingToDispose.Add(currentRoot);
                TryFreeWaitingToDispose();
            }
        }

        public void DereferenceRootNodeInternal(IRootNodeInternal root)
        {
            DereferenceRoot((IRootNode) root);
        }
    }
}
