using System.Numerics;

namespace Live2DCSharpSDK.Framework.Math;

/// <summary>
/// 数値計算などに使用するユーティリティクラス
/// </summary>
public static class CubismMath
{
    public const float Pi = 3.1415926535897932384626433832795f;
    public const float Epsilon = 0.00001f;

    /// <summary>
    /// 第一引数の値を最小値と最大値の範囲に収めた値を返す
    /// </summary>
    /// <param name="value">収められる値</param>
    /// <param name="min">範囲の最小値</param>
    /// <param name="max">範囲の最大値</param>
    /// <returns>最小値と最大値の範囲に収めた値</returns>
    public static float RangeF(float value, float min, float max)
    {
        if (value < min) value = min;
        else if (value > max) value = max;
        return value;
    }

    /// <summary>
    /// イージング処理されたサインを求める
    /// フェードイン・アウト時のイージングに利用できる
    /// </summary>
    /// <param name="value">イージングを行う値</param>
    /// <returns>イージング処理されたサイン値</returns>
    public static float GetEasingSine(float value)
    {
        if (value < 0.0f) return 0.0f;
        else if (value > 1.0f) return 1.0f;

        return 0.5f - 0.5f * MathF.Cos(value * Pi);
    }

    /// <summary>
    /// 角度値をラジアン値に変換します。
    /// </summary>
    /// <param name="degrees">角度値</param>
    /// <returns>角度値から変換したラジアン値</returns>
    public static float DegreesToRadian(float degrees)
    {
        return degrees / 180.0f * Pi;
    }

    /// <summary>
    /// 2つのベクトルからラジアン値を求めます。
    /// </summary>
    /// <param name="from">始点ベクトル</param>
    /// <param name="to">終点ベクトル</param>
    /// <returns>ラジアン値から求めた方向ベクトル</returns>
    public static float DirectionToRadian(Vector2 from, Vector2 to)
    {
        float q1 = MathF.Atan2(to.Y, to.X);
        float q2 = MathF.Atan2(from.Y, from.X);
        float ret = q1 - q2;

        while (ret < -Pi)
        {
            ret += Pi * 2.0f;
        }

        while (ret > Pi)
        {
            ret -= Pi * 2.0f;
        }

        return ret;
    }

    /// <summary>
    /// ラジアン値を方向ベクトルに変換します。
    /// </summary>
    /// <param name="totalAngle">ラジアン値</param>
    /// <returns>ラジアン値から変換した方向ベクトル</returns>
    public static Vector2 RadianToDirection(float totalAngle)
    {
        return new Vector2(MathF.Sin(totalAngle), MathF.Cos(totalAngle));
    }

    /// <summary>
    /// 三次方程式の三次項の係数が0になったときに補欠的に二次方程式の解をもとめる。
    /// a * x^2 + b * x + c = 0
    /// </summary>
    /// <param name="a">二次項の係数値</param>
    /// <param name="b">一次項の係数値</param>
    /// <param name="c">定数項の値</param>
    /// <returns>二次方程式の解</returns>
    public static float QuadraticEquation(float a, float b, float c)
    {
        if (MathF.Abs(a) < Epsilon)
        {
            if (MathF.Abs(b) < Epsilon)
            {
                return -c;
            }
            return -c / b;
        }

        return -(b + MathF.Sqrt(b * b - 4.0f * a * c)) / (2.0f * a);
    }

    /// <summary>
    /// カルダノの公式によってベジェのt値に該当する３次方程式の解を求める。
    /// 重解になったときには0.0～1.0の値になる解を返す。
    /// 
    /// a * x^3 + b * x^2 + c * x + d = 0
    /// </summary>
    /// <param name="a">三次項の係数値</param>
    /// <param name="b">二次項の係数値</param>
    /// <param name="c">一次項の係数値</param>
    /// <param name="d">定数項の値</param>
    /// <returns>0.0～1.0の間にある解</returns>
    public static float CardanoAlgorithmForBezier(float a, float b, float c, float d)
    {
        if (MathF.Abs(a) < Epsilon)
        {
            return RangeF(QuadraticEquation(b, c, d), 0.0f, 1.0f);
        }

        float ba = b / a;
        float ca = c / a;
        float da = d / a;

        float p = (3.0f * ca - ba * ba) / 3.0f;
        float p3 = p / 3.0f;
        float q = (2.0f * ba * ba * ba - 9.0f * ba * ca + 27.0f * da) / 27.0f;
        float q2 = q / 2.0f;
        float discriminant = q2 * q2 + p3 * p3 * p3;

        float center = 0.5f;
        float threshold = center + 0.01f;

        float root1;
        float u1;

        if (discriminant < 0.0f)
        {
            float mp3 = -p / 3.0f;
            float mp33 = mp3 * mp3 * mp3;
            float r = MathF.Sqrt(mp33);
            float t = -q / (2.0f * r);
            float cosphi = RangeF(t, -1.0f, 1.0f);
            float phi = MathF.Acos(cosphi);
            float crtr = MathF.Cbrt(r);
            float t1 = 2.0f * crtr;

            root1 = t1 * MathF.Cos(phi / 3.0f) - ba / 3.0f;
            if (MathF.Abs(root1 - center) < threshold)
            {
                return RangeF(root1, 0.0f, 1.0f);
            }

            float root2 = t1 * MathF.Cos((phi + 2.0f * Pi) / 3.0f) - ba / 3.0f;
            if (MathF.Abs(root2 - center) < threshold)
            {
                return RangeF(root2, 0.0f, 1.0f);
            }

            float root3 = t1 * MathF.Cos((phi + 4.0f * Pi) / 3.0f) - ba / 3.0f;
            return RangeF(root3, 0.0f, 1.0f);
        }
        else if (discriminant == 0.0f)
        {
            if (q2 < 0.0f)
            {
                u1 = MathF.Cbrt(-q2);
            }
            else
            {
                u1 = -MathF.Cbrt(q2);
            }

            root1 = 2.0f * u1 - ba / 3.0f;
            if (MathF.Abs(root1 - center) < threshold)
            {
                return RangeF(root1, 0.0f, 1.0f);
            }

            float root2 = -u1 - ba / 3.0f;
            return RangeF(root2, 0.0f, 1.0f);
        }

        float sd = MathF.Sqrt(discriminant);
        u1 = MathF.Cbrt(sd - q2);
        float v1 = MathF.Cbrt(sd + q2);
        root1 = u1 - v1 - ba / 3.0f;
        return RangeF(root1, 0.0f, 1.0f);
    }

}
