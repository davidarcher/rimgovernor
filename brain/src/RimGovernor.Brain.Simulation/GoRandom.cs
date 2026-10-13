namespace RimGovernor.Brain.Simulation;

/// <summary>
/// Go's <c>math/rand/v2</c> PCG source and the <c>Rand</c> methods the simulators draw: the same stream for the
/// same seed pair, so a seeded fight draws what the Go simulator drew. <see cref="NextNormal"/> is Go's
/// ziggurat (tables copied from the standard library), so even the normal draws replay.
/// </summary>
public sealed class GoRandom
{
    // 128-bit PCG-DXSM state, high and low halves, as in Go's rand.PCG.
    private ulong _hi;
    private ulong _lo;

    /// <summary>Go <c>rand.New(rand.NewPCG(seed1, seed2))</c>.</summary>
    public GoRandom(ulong seed1, ulong seed2)
    {
        _hi = seed1;
        _lo = seed2;
    }

    /// <summary>Go <c>Uint64</c>.</summary>
    public ulong NextUInt64()
    {
        const ulong mulHi = 2549297995355413924UL;
        const ulong mulLo = 4865540595714422341UL;
        const ulong incHi = 6364136223846793005UL;
        const ulong incLo = 1442695040888963407UL;
        const ulong cheapMul = 0xda942042e4dd58b5UL;

        // state = state * mul + inc
        var hi = Math.BigMul(_lo, mulLo, out var lo);
        unchecked
        {
            hi += (_hi * mulLo) + (_lo * mulHi);
            var sum = lo + incLo;
            var carry = sum < lo ? 1UL : 0UL;
            lo = sum;
            hi += incHi + carry;
        }
        _lo = lo;
        _hi = hi;

        // DXSM "double xorshift multiply"
        unchecked
        {
            hi ^= hi >> 32;
            hi *= cheapMul;
            hi ^= hi >> 48;
            hi *= lo | 1;
        }
        return hi;
    }

    /// <summary>Go <c>Float64</c>: [0, 1) with 53 random bits.</summary>
    public double NextDouble() => (double)((NextUInt64() << 11) >> 11) / (1UL << 53);

    /// <summary>Go <c>uint64n</c>: unbiased in [0, n); n must be positive.</summary>
    public ulong NextUInt64(ulong n)
    {
        if ((n & (n - 1)) == 0)
        {
            return NextUInt64() & (n - 1);
        }
        var hi = Math.BigMul(NextUInt64(), n, out var lo);
        if (lo < n)
        {
            var thresh = unchecked(0UL - n) % n;
            while (lo < thresh)
            {
                hi = Math.BigMul(NextUInt64(), n, out lo);
            }
        }
        return hi;
    }

    /// <summary>Go <c>IntN</c>.</summary>
    public int NextInt(int n)
    {
        if (n <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(n), "invalid argument to IntN");
        }
        return (int)NextUInt64((ulong)n);
    }

    /// <summary>Go <c>Shuffle</c>: Fisher-Yates from the end.</summary>
    public void Shuffle(int n, Action<int, int> swap)
    {
        for (var i = n - 1; i > 0; i--)
        {
            var j = (int)NextUInt64((ulong)(i + 1));
            swap(i, j);
        }
    }

    /// <summary>Go <c>NormFloat64</c>: standard normal by the Marsaglia-Tsang ziggurat.</summary>
    public double NextNormal()
    {
        const double rn = 3.442619855899;
        while (true)
        {
            var u = NextUInt64();
            var j = unchecked((int)(uint)u);
            var i = (int)((u >> 32) & 0x7F);
            var x = j * (double)_wn[i];
            if (AbsInt32(j) < _kn[i])
            {
                return x;
            }
            if (i == 0)
            {
                while (true)
                {
                    x = -Math.Log(NextDouble()) * (1.0 / rn);
                    var y = -Math.Log(NextDouble());
                    if (y + y >= x * x)
                    {
                        break;
                    }
                }
                return j > 0 ? rn + x : -rn - x;
            }
            if (_fn[i] + ((float)NextDouble() * (_fn[i - 1] - _fn[i])) < (float)Math.Exp(-.5 * x * x))
            {
                return x;
            }
        }
    }

    private static uint AbsInt32(int i) => i < 0 ? unchecked((uint)-i) : (uint)i;

    private static readonly uint[] _kn =
    [
        0x76ad2212u, 0x0u, 0x600f1b53u, 0x6ce447a6u, 0x725b46a2u, 0x7560051du, 0x774921ebu, 0x789a25bdu,
        0x799045c3u, 0x7a4bce5du, 0x7adf629fu, 0x7b5682a6u, 0x7bb8a8c6u, 0x7c0ae722u, 0x7c50cce7u, 0x7c8cec5bu,
        0x7cc12cd6u, 0x7ceefed2u, 0x7d177e0bu, 0x7d3b8883u, 0x7d5bce6cu, 0x7d78dd64u, 0x7d932886u, 0x7dab0e57u,
        0x7dc0dd30u, 0x7dd4d688u, 0x7de73185u, 0x7df81ceau, 0x7e07c0a3u, 0x7e163efau, 0x7e23b587u, 0x7e303dfdu,
        0x7e3beec2u, 0x7e46db77u, 0x7e51155du, 0x7e5aabb3u, 0x7e63abf7u, 0x7e6c222cu, 0x7e741906u, 0x7e7b9a18u,
        0x7e82adfau, 0x7e895c63u, 0x7e8fac4bu, 0x7e95a3fbu, 0x7e9b4924u, 0x7ea0a0efu, 0x7ea5b00du, 0x7eaa7ac3u,
        0x7eaf04f3u, 0x7eb3522au, 0x7eb765a5u, 0x7ebb4259u, 0x7ebeeafdu, 0x7ec2620au, 0x7ec5a9c4u, 0x7ec8c441u,
        0x7ecbb365u, 0x7ece78edu, 0x7ed11671u, 0x7ed38d62u, 0x7ed5df12u, 0x7ed80cb4u, 0x7eda175cu, 0x7edc0005u,
        0x7eddc78eu, 0x7edf6ebfu, 0x7ee0f647u, 0x7ee25ebeu, 0x7ee3a8a9u, 0x7ee4d473u, 0x7ee5e276u, 0x7ee6d2f5u,
        0x7ee7a620u, 0x7ee85c10u, 0x7ee8f4cdu, 0x7ee97047u, 0x7ee9ce59u, 0x7eea0ecau, 0x7eea3147u, 0x7eea3568u,
        0x7eea1aabu, 0x7ee9e071u, 0x7ee98602u, 0x7ee90a88u, 0x7ee86d08u, 0x7ee7ac6au, 0x7ee6c769u, 0x7ee5bc9cu,
        0x7ee48a67u, 0x7ee32efcu, 0x7ee1a857u, 0x7edff42fu, 0x7ede0ffau, 0x7edbf8d9u, 0x7ed9ab94u, 0x7ed7248du,
        0x7ed45faeu, 0x7ed1585cu, 0x7ece095fu, 0x7eca6ccbu, 0x7ec67be2u, 0x7ec22eeeu, 0x7ebd7d1au, 0x7eb85c35u,
        0x7eb2c075u, 0x7eac9c20u, 0x7ea5df27u, 0x7e9e769fu, 0x7e964c16u, 0x7e8d44bau, 0x7e834033u, 0x7e781728u,
        0x7e6b9933u, 0x7e5d8a1au, 0x7e4d9dedu, 0x7e3b737au, 0x7e268c2fu, 0x7e0e3ff5u, 0x7df1aa5du, 0x7dcf8c72u,
        0x7da61a1eu, 0x7d72a0fbu, 0x7d30e097u, 0x7cd9b4abu, 0x7c600f1au, 0x7ba90bdcu, 0x7a722176u, 0x77d664e5u,
    ];

    private static readonly float[] _wn =
    [
        1.7290405e-09f, 1.2680929e-10f, 1.6897518e-10f, 1.9862688e-10f, 2.2232431e-10f, 2.4244937e-10f, 2.601613e-10f, 2.7611988e-10f,
        2.9073963e-10f, 3.042997e-10f, 3.1699796e-10f, 3.289802e-10f, 3.4035738e-10f, 3.5121603e-10f, 3.616251e-10f, 3.7164058e-10f,
        3.8130857e-10f, 3.9066758e-10f, 3.9975012e-10f, 4.08584e-10f, 4.1719309e-10f, 4.2559822e-10f, 4.338176e-10f, 4.418672e-10f,
        4.497613e-10f, 4.5751258e-10f, 4.651324e-10f, 4.7263105e-10f, 4.8001775e-10f, 4.87301e-10f, 4.944885e-10f, 5.015873e-10f,
        5.0860405e-10f, 5.155446e-10f, 5.2241467e-10f, 5.2921934e-10f, 5.359635e-10f, 5.426517e-10f, 5.4928817e-10f, 5.5587696e-10f,
        5.624219e-10f, 5.6892646e-10f, 5.753941e-10f, 5.818282e-10f, 5.882317e-10f, 5.946077e-10f, 6.00959e-10f, 6.072884e-10f,
        6.135985e-10f, 6.19892e-10f, 6.2617134e-10f, 6.3243905e-10f, 6.386974e-10f, 6.449488e-10f, 6.511956e-10f, 6.5744005e-10f,
        6.6368433e-10f, 6.699307e-10f, 6.7618144e-10f, 6.824387e-10f, 6.8870465e-10f, 6.949815e-10f, 7.012715e-10f, 7.075768e-10f,
        7.1389966e-10f, 7.202424e-10f, 7.266073e-10f, 7.329966e-10f, 7.394128e-10f, 7.4585826e-10f, 7.5233547e-10f, 7.58847e-10f,
        7.653954e-10f, 7.719835e-10f, 7.7861395e-10f, 7.852897e-10f, 7.920138e-10f, 7.987892e-10f, 8.0561924e-10f, 8.125073e-10f,
        8.194569e-10f, 8.2647167e-10f, 8.3355556e-10f, 8.407127e-10f, 8.479473e-10f, 8.55264e-10f, 8.6266755e-10f, 8.7016316e-10f,
        8.777562e-10f, 8.8545243e-10f, 8.932582e-10f, 9.0117996e-10f, 9.09225e-10f, 9.174008e-10f, 9.2571584e-10f, 9.341788e-10f,
        9.427997e-10f, 9.515889e-10f, 9.605579e-10f, 9.697193e-10f, 9.790869e-10f, 9.88676e-10f, 9.985036e-10f, 1.0085882e-09f,
        1.0189509e-09f, 1.0296151e-09f, 1.0406069e-09f, 1.0519566e-09f, 1.063698e-09f, 1.0758702e-09f, 1.0885183e-09f, 1.1016947e-09f,
        1.1154611e-09f, 1.1298902e-09f, 1.1450696e-09f, 1.1611052e-09f, 1.1781276e-09f, 1.1962995e-09f, 1.2158287e-09f, 1.2369856e-09f,
        1.2601323e-09f, 1.2857697e-09f, 1.3146202e-09f, 1.347784e-09f, 1.3870636e-09f, 1.4357403e-09f, 1.5008659e-09f, 1.6030948e-09f,
    ];

    private static readonly float[] _fn =
    [
        1f, 0.9635997f, 0.9362827f, 0.9130436f, 0.89228165f, 0.87324303f, 0.8555006f, 0.8387836f,
        0.8229072f, 0.8077383f, 0.793177f, 0.7791461f, 0.7655842f, 0.7524416f, 0.73967725f, 0.7272569f,
        0.7151515f, 0.7033361f, 0.69178915f, 0.68049186f, 0.6694277f, 0.658582f, 0.6479418f, 0.63749546f,
        0.6272325f, 0.6171434f, 0.6072195f, 0.5974532f, 0.58783704f, 0.5783647f, 0.56903f, 0.5598274f,
        0.5507518f, 0.54179835f, 0.5329627f, 0.52424055f, 0.5156282f, 0.50712204f, 0.49871865f, 0.49041483f,
        0.48220766f, 0.4740943f, 0.46607214f, 0.4581387f, 0.45029163f, 0.44252872f, 0.43484783f, 0.427247f,
        0.41972435f, 0.41227803f, 0.40490642f, 0.39760786f, 0.3903808f, 0.3832238f, 0.37613547f, 0.36911446f,
        0.3621595f, 0.35526937f, 0.34844297f, 0.34167916f, 0.33497685f, 0.3283351f, 0.3217529f, 0.3152294f,
        0.30876362f, 0.30235484f, 0.29600215f, 0.28970486f, 0.2834622f, 0.2772735f, 0.27113807f, 0.2650553f,
        0.25902456f, 0.2530453f, 0.24711695f, 0.241239f, 0.23541094f, 0.22963232f, 0.2239027f, 0.21822165f,
        0.21258877f, 0.20700371f, 0.20146611f, 0.19597565f, 0.19053204f, 0.18513499f, 0.17978427f, 0.17447963f,
        0.1692209f, 0.16400786f, 0.15884037f, 0.15371831f, 0.14864157f, 0.14361008f, 0.13862377f, 0.13368265f,
        0.12878671f, 0.12393598f, 0.119130544f, 0.11437051f, 0.10965602f, 0.104987256f, 0.10036444f, 0.095787846f,
        0.0912578f, 0.08677467f, 0.0823389f, 0.077950984f, 0.073611505f, 0.06932112f, 0.06508058f, 0.06089077f,
        0.056752663f, 0.0526674f, 0.048636295f, 0.044660863f, 0.040742867f, 0.03688439f, 0.033087887f, 0.029356318f,
        0.025693292f, 0.022103304f, 0.018592102f, 0.015167298f, 0.011839478f, 0.008624485f, 0.005548995f, 0.0026696292f,
    ];
}
