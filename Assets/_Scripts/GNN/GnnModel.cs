using System;
using UnityEngine;

[Serializable]
public class GnnSageLayerWeights
{
    public int inDim;
    public int outDim;
    public float[] selfWeight;  // outDim x inDim (row-major)
    public float[] neighWeight; // outDim x inDim (row-major)
    public float[] bias;        // outDim
}

[Serializable]
public class GnnDenseWeights
{
    public int inDim;
    public int outDim;
    public float[] weight; // outDim x inDim (row-major)
    public float[] bias;   // outDim
}

/// <summary>GNN/train_gnn.py tarafından üretilen ağırlık dosyası.</summary>
[Serializable]
public class GnnModelWeights
{
    public int version;
    public string architecture;
    public int inputDim;
    public int numClasses;
    public string[] featureNames;
    public string[] classNames;
    public float[] featureMean;
    public float[] featureStd;
    public GnnSageLayerWeights[] layers;
    public GnnDenseWeights head;
}

/// <summary>
/// GraphSAGE (mean aggregator) çıkarımı, saf C#. Sentis / ek paket gerektirmez,
/// Quest 3'te birkaç bin düğüm için milisaniyeler sürer.
///
///   h_i^(l+1) = ReLU( W_self h_i^(l) + W_neigh * mean_{j in N(i)} h_j^(l) + b )
///   p_i       = softmax( W_head h_i^(L) + b_head )
/// </summary>
public sealed class GnnModel
{
    private readonly GnnModelWeights w;

    public int NumClasses => w.numClasses;
    public int InputDim => w.inputDim;

    private GnnModel(GnnModelWeights weights)
    {
        w = weights;
    }

    public static GnnModel FromJson(string json, out string error)
    {
        error = null;
        GnnModelWeights weights;
        try
        {
            weights = JsonUtility.FromJson<GnnModelWeights>(json);
        }
        catch (Exception e)
        {
            error = "Ağırlık JSON'u okunamadı: " + e.Message;
            return null;
        }

        if (weights == null || weights.layers == null || weights.head == null)
        {
            error = "Ağırlık dosyası eksik (layers/head yok).";
            return null;
        }

        if (weights.inputDim != GnnGraphBuilder.FeatureCount)
        {
            error = $"Model {weights.inputDim} özellik bekliyor, builder {GnnGraphBuilder.FeatureCount} üretiyor. Modeli yeniden eğitin.";
            return null;
        }

        if (weights.featureNames != null)
        {
            for (int i = 0; i < weights.featureNames.Length && i < GnnGraphBuilder.FeatureCount; i++)
            {
                if (weights.featureNames[i] != GnnGraphBuilder.FeatureNames[i])
                {
                    error = $"Özellik sırası uyuşmuyor: '{weights.featureNames[i]}' != '{GnnGraphBuilder.FeatureNames[i]}'. Modeli yeniden eğitin.";
                    return null;
                }
            }
        }

        return new GnnModel(weights);
    }

    /// <summary>Her düğüm için sınıf olasılıkları (N * numClasses).</summary>
    public float[] Predict(GnnGraph graph)
    {
        int n = graph.NodeCount;
        int dim = w.inputDim;

        // Standardizasyon (eğitimdeki ortalama / std)
        float[] h = new float[n * dim];
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k < dim; k++)
            {
                float std = w.featureStd[k] > 1e-6f ? w.featureStd[k] : 1f;
                h[i * dim + k] = (graph.Features[i * dim + k] - w.featureMean[k]) / std;
            }
        }

        foreach (GnnSageLayerWeights layer in w.layers)
        {
            h = SageLayer(graph, h, layer);
            dim = layer.outDim;
        }

        GnnDenseWeights head = w.head;
        int c = head.outDim;
        float[] probs = new float[n * c];
        for (int i = 0; i < n; i++)
        {
            float max = float.MinValue;
            for (int o = 0; o < c; o++)
            {
                float sum = head.bias[o];
                int row = o * head.inDim;
                for (int k = 0; k < head.inDim; k++) sum += head.weight[row + k] * h[i * dim + k];
                probs[i * c + o] = sum;
                max = Mathf.Max(max, sum);
            }

            float total = 0f;
            for (int o = 0; o < c; o++)
            {
                float e = Mathf.Exp(probs[i * c + o] - max);
                probs[i * c + o] = e;
                total += e;
            }
            for (int o = 0; o < c; o++) probs[i * c + o] /= total;
        }

        return probs;
    }

    private static float[] SageLayer(GnnGraph graph, float[] h, GnnSageLayerWeights layer)
    {
        int n = graph.NodeCount;
        int inDim = layer.inDim;
        int outDim = layer.outDim;
        float[] agg = new float[inDim];
        float[] output = new float[n * outDim];

        for (int i = 0; i < n; i++)
        {
            // Komşu ortalaması (komşusuz düğümde sıfır vektör)
            Array.Clear(agg, 0, inDim);
            int start = graph.NeighborOffsets[i];
            int end = graph.NeighborOffsets[i + 1];
            if (end > start)
            {
                for (int e = start; e < end; e++)
                {
                    int j = graph.NeighborIndices[e];
                    for (int k = 0; k < inDim; k++) agg[k] += h[j * inDim + k];
                }
                float inv = 1f / (end - start);
                for (int k = 0; k < inDim; k++) agg[k] *= inv;
            }

            for (int o = 0; o < outDim; o++)
            {
                float sum = layer.bias[o];
                int row = o * inDim;
                for (int k = 0; k < inDim; k++)
                    sum += layer.selfWeight[row + k] * h[i * inDim + k] + layer.neighWeight[row + k] * agg[k];
                output[i * outDim + o] = sum > 0f ? sum : 0f; // ReLU
            }
        }

        return output;
    }
}
