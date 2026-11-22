namespace HongguoTV.Core.Crypto;

internal sealed class AesV3
{
    private static readonly int[][] V5Con =
    {
        new[] { 2, 0, 3, 1 }, new[] { 1, 3, 2, 0 },
        new[] { 3, 2, 1, 0 }, new[] { 2, 3, 1, 0 },
    };

    private static readonly int[][] LegacyCon =
    {
        new[] { 1, 0, 2, 3 }, new[] { 2, 0, 3, 1 },
        new[] { 0, 1, 3, 2 }, new[] { 1, 0, 2, 3 },
    };

    private static readonly int[][] V5Order =
    {
        new[] { 0,9,14,15,4,13,2,7,8,1,6,3,12,5,10,11 },
        new[] { 0,9,14,11,4,13,2,3,8,1,6,15,12,5,10,7 },
        new[] { 0,9,14,3,4,13,2,15,8,1,6,11,12,5,10,7 },
        new[] { 0,9,14,15,4,13,2,3,8,1,6,11,12,5,10,7 },
    };

    private static readonly int[][] LegacyOrder =
    {
        new[] { 0,9,14,11,4,13,2,7,8,1,6,15,12,5,10,3 },
        new[] { 0,9,14,15,4,13,2,7,8,1,6,3,12,5,10,11 },
        new[] { 0,9,14,7,4,13,2,11,8,1,6,3,12,5,10,15 },
        new[] { 0,9,14,11,4,13,2,7,8,1,6,15,12,5,10,3 },
    };

    private static readonly int[] V5Initial =
    {
        unchecked((int)0xbbb5de72), unchecked((int)0xd4638d5b),
        unchecked((int)0x92a5f73b), unchecked((int)0x89f8a1c9),
    };

    private static readonly int[] LegacyInitial =
    {
        unchecked((int)0xca025ddc), unchecked((int)0x823dc546),
        unchecked((int)0xc9420583), unchecked((int)0xc298225f),
    };

    private readonly int[] _box;
    private readonly int[] _con;
    private readonly int[] _order;
    private readonly int[][] _keys;
    private readonly bool _v5;

    public AesV3(byte[] key, int timestamp, bool v5 = false)
    {
        _v5 = v5;
        var word = timestamp & 3;
        var table = v5 ? V5Constants.Sbox : VendorConstants.Sbox;
        _box = Enumerable.Range(0, 256).Select(i => (int)table[word * 256 + i]).ToArray();
        _con = v5 ? V5Con[word] : LegacyCon[word];
        _order = v5 ? V5Order[word] : LegacyOrder[word];
        var initial = v5 ? V5Initial[word] : LegacyInitial[word];

        var seed = Bytes.Join(Bytes.Le32(initial), Bytes.Le32(initial), Bytes.Le32(initial), Bytes.Le32(initial));
        var words = Enumerable.Range(0, 48).Select(i => (int)(byte)(seed[i % seed.Length] ^ key[i % key.Length])).ToArray();

        var rounds = 8;
        for (var i = 4; i < 12; i++)
        {
            var at = 4 * (i - 1);
            var a = words[at];
            var b = words[at + 1];
            var c = words[at + 2];
            var d = words[at + 3];
            if (i % 4 == 0)
            {
                var t = ((initial >> (rounds & 24)) ^ _box[b]) & 255;
                b = _box[c];
                c = _box[d];
                d = _box[a];
                a = t;
            }
            rounds += 2;
            words[at + 4] = unchecked(a ^ words[at - 12]);
            words[at + 5] = unchecked(b ^ words[at - 11]);
            words[at + 6] = unchecked(c ^ words[at - 10]);
            words[at + 7] = unchecked(d ^ words[at - 9]);
        }
        _keys = Enumerable.Range(0, 12).Select(i => words.Skip(i * 4).Take(4).ToArray()).ToArray();
    }

    private byte[] Block(byte[] value)
    {
        var rows = Enumerable.Range(0, 4)
            .Select(i => Enumerable.Range(0, 4).Select(j => (int)value[i * 4 + j]).ToArray()).ToArray();

        void AddCon(int offset)
        {
            for (var i = 0; i < 4; i++)
                for (var j = 0; j < 4; j++)
                    rows[i][j] = unchecked(rows[i][j] ^ _keys[offset + i][_con[j]]);
        }

        AddCon(0);
        for (var round = 1; round <= 2; round++)
        {
            for (var i = 0; i < 4; i++)
                for (var j = 0; j < 4; j++)
                    rows[i][j] = _box[rows[i][j]];

            var before = rows.Select(row => (int[])row.Clone()).ToArray();
            for (var i = 0; i < 4; i++) rows[i] = (int[])before[_con[i]].Clone();

            var flat = rows.SelectMany(row => row).ToArray();
            for (var i = 0; i < 16; i++) rows[i / 4][i % 4] = flat[_order[i]];

            if (round == 1)
            {
                for (var i = 0; i < 4; i++)
                {
                    var old = (int[])rows[i].Clone();
                    rows[i] = Enumerable.Range(0, 4).Select(j => old[_con[j]]).ToArray();
                }
                static int Xt(int x) => (((x << 1) ^ ((x & 128) != 0 ? 0x1b : 0)) & 255);
                for (var i = 0; i < 4; i++)
                {
                    var t = rows[0][i] ^ rows[1][i] ^ rows[2][i] ^ rows[3][i];
                    var u = rows[0][i];
                    rows[0][i] = unchecked(rows[0][i] ^ t ^ Xt(rows[0][i] ^ rows[1][i]));
                    rows[1][i] = unchecked(rows[1][i] ^ t ^ Xt(rows[1][i] ^ rows[2][i]));
                    rows[2][i] = unchecked(rows[2][i] ^ t ^ Xt(rows[2][i] ^ rows[3][i]));
                    rows[3][i] = unchecked(rows[3][i] ^ t ^ Xt(rows[3][i] ^ u));
                }
            }
            AddCon(round * 4);
        }
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
                rows[i][j] = unchecked(rows[i][j] ^ _keys[4 + i][j]);

        return rows.SelectMany(row => row).Select(v => (byte)v).ToArray();
    }

    public byte[] Encrypt(byte[] source, byte[] iv)
    {
        if (source.Length < 248) throw new ArgumentException("source too short");
        var plaintext = new byte[32];
        var wire = _v5 ? new[] { 6, 5, 3, 4, 2, 7, 1, 0 } : new[] { 5, 6, 2, 4, 3, 1, 0, 7 };
        var compact = _v5 ? new[] { 1, 7, 3, 6, 2, 0, 4, 5 } : new[] { 1, 6, 0, 7, 2, 4, 5, 3 };
        for (var i = 0; i < 31; i++)
            for (var j = 0; j < 8; j++)
                plaintext[i] = (byte)(plaintext[i] | (((source[i * 8 + j] >> wire[j]) & 1) << compact[j]));
        plaintext[31] = 1;
        var first = Block(Bytes.Xor(plaintext.AsSpan(0, 16), iv));
        var key = Bytes.Join(first, Block(Bytes.Xor(plaintext.AsSpan(16, 16), first)));
        var output = (byte[])source.Clone();
        for (var i = 0; i < 31; i++)
            for (var j = 0; j < 8; j++)
            {
                var at = i * 8 + j;
                output[at] = (byte)((output[at] & ~(1 << wire[j])) | (((key[i] >> compact[j]) & 1) << wire[j]));
            }
        return Bytes.Join(new[] { key[^1] }, output);
    }
}
