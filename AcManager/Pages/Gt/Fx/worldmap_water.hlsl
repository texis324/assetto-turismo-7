// worldmap_water.hlsl — [fork] ワールドマップの海（WPF ShaderEffect・ps_3_0）2026-09-27
//   要望「画像の波部分が動いてほしい。砂浜の打ち寄せる波とか。揺れるヨットとか船とか」
//   入力 s0 = 4 枚重ねの地図絵（MapLayers）/ s1 = マスク worldmap_mask.png / s2 = ゆらぎ worldmap_noise.png（make_mask.py で作成）
//     R=水面の度合い  G=砂浜の波打ち際（手でなぞった線）からの符号付き距離(0.5=波打ち際・1=沖64px・0=陸32px)  B=砂浜の岸らしさ
//   ① 船: 喫水線を支点に、楕円の範囲だけ小さく傾けて揺らす（切り抜き不要・周りの水も一緒に少し動く）
//   ② 水面: 1px 弱のゆらぎ＋うっすらきらめき（陸の色を拾わないようマスクで抑える）
//   ③ 砂浜: 沖から寄せる淡いうねり→泡の前線が砂の上まで駆け上がり、ゆっくり引く。届いた範囲は濡れて少し暗い
//   コンパイル: compile_fx.ps1（Windows 標準の d3dcompiler_47.dll で ps_3_0 に）
sampler2D Input : register(s0);
sampler2D Mask  : register(s1);
sampler2D Noise : register(s2);   // 岸沿いのゆらぎ（グレー）。★マスクの透明度には入れない（WPF が色×透明度に直して R/G/B が狂う）

float4 P0   : register(c0);   // x=時間(秒)  z=1px(u)  w=1px(v)
float4 Foam : register(c1);   // rgb=泡の色（時間帯の色味）  a=泡の強さ
// 船 5 隻: BoatA = xy 支点(uv) / zw 揺らす範囲の半径(uv)   BoatB = x 範囲の中心の上下ずれ(uv) / y 傾きの振幅(rad) / z 角速度(rad/s) / w 位相
float4 BoatA0 : register(c2);  float4 BoatB0 : register(c3);
float4 BoatA1 : register(c4);  float4 BoatB1 : register(c5);
float4 BoatA2 : register(c6);  float4 BoatB2 : register(c7);
float4 BoatA3 : register(c8);  float4 BoatB3 : register(c9);
float4 BoatA4 : register(c10); float4 BoatB4 : register(c11);

float4 Tex(sampler2D s, float2 uv) { return tex2Dlod(s, float4(uv, 0, 0)); }

float2 BoatOffset(float2 uv, float4 A, float4 B, float t, float2 px) {
    float2 ctr = A.xy + float2(0, B.x);
    float2 q = (uv - ctr) / max(A.zw, float2(1e-5, 1e-5));
    float w = saturate(1 - dot(q, q));
    w = w * w * (3 - 2 * w);
    float ang = B.y * sin(t * B.z + B.w) * w;
    float bob = 0.9 * sin(t * B.z * 0.83 + B.w + 1.7) * w;          // px
    float2 d = (uv - A.xy) / px;                                     // px 単位で回す（縦横比を崩さない）
    float c = cos(ang), s = sin(ang);
    float2 dr = float2(c * d.x - s * d.y, s * d.x + c * d.y);
    return (dr - d) * px + float2(0, bob * px.y);
}

bool NearBoat(float2 uv, float4 A, float4 B) {
    float2 ctr = A.xy + float2(0, B.x);
    float2 d = abs(uv - ctr);
    return d.x < A.z && d.y < A.w;
}

float4 main(float2 uv : TEXCOORD) : COLOR {
    float t = P0.x;
    float2 px = P0.zw;

    // 近道: 水でも砂浜でもなく、船の近くでもない点（画面の大半＝陸）は絵をそのまま返す（GPU の負担を減らす・2026-09-28）
    float4 m0 = Tex(Mask, uv);
    if (m0.r < 0.004 && m0.b < 0.004 && P0.y < 0.5
            && !NearBoat(uv, BoatA0, BoatB0) && !NearBoat(uv, BoatA1, BoatB1) && !NearBoat(uv, BoatA2, BoatB2)
            && !NearBoat(uv, BoatA3, BoatB3) && !NearBoat(uv, BoatA4, BoatB4)) {
        return Tex(Input, uv);
    }

    // ① 船
    float2 suv = uv;
    suv += BoatOffset(uv, BoatA0, BoatB0, t, px);
    suv += BoatOffset(uv, BoatA1, BoatB1, t, px);
    suv += BoatOffset(uv, BoatA2, BoatB2, t, px);
    suv += BoatOffset(uv, BoatA3, BoatB3, t, px);
    suv += BoatOffset(uv, BoatA4, BoatB4, t, px);

    // ② 水面のゆらぎ
    float4 m = Tex(Mask, suv);
    float water = m.r;
    float2 wob;
    wob.x = sin(suv.y * 180.0 + t * 1.6 + sin(suv.x * 55.0 + t * 0.5) * 1.8) + 0.5 * sin(suv.y * 420.0 - t * 2.3);
    wob.y = cos(suv.x * 140.0 - t * 1.2 + sin(suv.y * 70.0 - t * 0.35) * 1.8) + 0.5 * cos(suv.x * 380.0 + t * 2.1);
    float2 wuv = suv + wob * px * 0.65 * water;
    float wm = Tex(Mask, wuv).r;
    wuv = lerp(suv, wuv, wm * water);                                // 陸の色を水面に引き込まない
    float4 col = Tex(Input, wuv);
    // きらめき(規則的な sin×sin)は細かい網目に見えたので外した（2026-09-28）


    // ③ 砂浜に打ち寄せる波
    float beach = m.b;
    float g = m.g;
    float sd = g >= 0.5 ? (g - 0.5) * 128.0 : -(0.5 - g) * 64.0;      // px（+ 沖 / - 砂）
    if (beach > 0.02 && sd < 60.0 && sd > -30.0) {
        float seed = Tex(Noise, suv).r;
        // 周期は全体で共通（場所ごとに変えると時間とともに位相がずれて、沖に斜めの泡の線が出た・2026-09-28）
        // 場所ごとのずれは穏やかに（大きいと岸沿いに前線が急に前後して、泡の線が斜めに走った）。frac の段差も使わない
        // 1 回 9 秒: 寄せる 4 秒 → 駆け上がった所で一瞬とどまる → 約 5 秒かけてゆっくり引く
        //   （7 秒・引き 2.8 秒で引き始めが一番速かった＝本人「さざ波、ちょっと戻るの早くないか」2026-09-28）
        float period = 9.0;
        float ph = frac(t / period + seed * 0.22);
        float runup = lerp(6.0, 13.0, 0.5 + 0.5 * sin(seed * 9.0));
        // 前線のでこぼこ・泡の途切れは、なめらかなノイズ(A)を細かく折り返して作る（sin×sin の格子は網目・点々に見えた）
        float jag = 1.2 * sin(seed * 60.0 + 0.7) + 0.6 * sin(seed * 130.0 - 1.3);
        float front, str, width;
        if (ph < 0.45) {                                             // 寄せる（だんだん速く）
            float k = ph / 0.45; k = k * k;
            front = lerp(26.0, -runup, k) + jag;
            str = smoothstep(0.0, 0.5, k);
            width = lerp(2.5, 4.5, k);
        } else {                                                     // とどまって → ゆっくり引く（動き出しも止まり際もなめらか）
            float k = smoothstep(0.08, 1.0, (ph - 0.45) / 0.55);
            front = lerp(-runup, 8.0, k) + jag;
            str = 1 - k;
            width = 4.5;
        }
        // 岸の法線（G の勾配＝沖の向き）で、沖側の水の色を拾う（朝・夕・夜の絵の色がそのまま使える）
        float gx = Tex(Mask, suv + float2(px.x * 2, 0)).g - Tex(Mask, suv - float2(px.x * 2, 0)).g;
        float gy = Tex(Mask, suv + float2(0, px.y * 2)).g - Tex(Mask, suv - float2(0, px.y * 2)).g;
        float2 n = float2(gx, gy);
        float nl = length(n);
        n = nl > 1e-5 ? n / nl : float2(0, 1);
        float4 waterCol = Tex(Input, suv + n * px * (max(-sd, 0) + 8.0));
        // 水の膜（前線が砂の上にある間、前線〜波打ち際）
        float sheet = (sd < 0 && sd > front) ? saturate((sd - front) / 3.0) : 0;
        col.rgb = lerp(col.rgb, waterCol.rgb, sheet * 0.55 * str * beach);
        // 濡れた砂
        float wet = sd < 0 ? saturate(1 + sd / runup) : saturate(1 - sd / 4.0);   // 水側にもなめらかにつなぐ（段差が縁取りに見えた）
        col.rgb *= 1 - 0.10 * wet * beach;
        // 泡の前線（ところどころ途切れる）＋沖から寄せる淡いうねり
        float broken = 0.72 + 0.28 * sin(seed * 95.0 + t * 0.35) * sin(seed * 41.0 - t * 0.25);
        // 泡は波打ち際の近くで崩れてから（沖では泡を立てず、淡いうねりの帯だけ）
        float breaking = smoothstep(12.0, 3.0, front);
        float foamLine = saturate(1 - abs(sd - front) / width) * broken * breaking;
        float swellBand = saturate(1 - abs(sd - front) / 9.0) * (1 - breaking) * 0.12;
        // 沖から寄せるうねり: 細い線は等高線のように見えたので、ゆるい明暗の帯にする（2026-09-28）
        float swell = 0;
        if (sd > 0) {
            swell = (0.5 + 0.5 * sin(sd * 0.39 + t * 6.2832 / period * 2.0 + seed * 6.0)) * saturate(1 - sd / 45.0) * saturate(sd / 6.0) * 0.10;
        }
        // 先端の後ろ（沖側）に淡い泡の尾
        float trail = (sd > front) ? saturate(1 - (sd - front) / 10.0) * 0.35 * breaking * broken : 0;
        float f = saturate((foamLine + trail + swellBand) * str + swell) * beach * Foam.a;
        col.rgb = lerp(col.rgb, Foam.rgb, f);
    }
    // 確認用（設定 debug=1）: マスクの波打ち際を赤、水面の縁を青で描く
    if (P0.y > 0.5) {
        if (beach > 0.3 && abs(sd) < 1.2) col.rgb = float3(1, 0, 0);
        float r0 = Tex(Mask, uv).r;
        if (r0 > 0.35 && r0 < 0.65) col.rgb = float3(0, 0.4, 1);
    }
    return col;
}
