"""
İnşaat sahası güvenli / dikkat / tehlikeli alan sınıflandırması için GraphSAGE eğitimi.

Girdi : GNN/data/*_graph_*.json  (Unity: Tools > GNN > Eğitim Verisini ve Grafı Dışa Aktar)
Çıktı : Assets/Resources/GNN/gnn_weights.json  (Unity'deki GnnModel.cs bunu okur)
        GNN/report/metrics.json                (akademik rapor için metrikler)

Model (GnnModel.cs ile birebir aynı):
    h_i^(l+1) = ReLU( W_self h_i^(l) + W_neigh * mean_{j in N(i)} h_j^(l) + b )
    p_i       = softmax( W_head h_i^(L) + b_head )

Değerlendirme: Düğümler dünya konumuna göre 2 m'lik uzamsal bloklara ayrılır ve bloklar
katmanlı olarak 5 kata dağıtılır (aynı bölgenin kaydırılmış grid kopyaları aynı katta kalır,
veri sızıntısı engellenir). 3 tekrarlı 5-katlı çapraz doğrulama ortalama ± std raporlanır; ek olarak
etiket kutularının kenarları için 1-komşu toleranslı metrikler verilir. Oyuna giden nihai
model, erken durdurma katı dışındaki tüm verilerle eğitilir.

Kullanım:
    python3 GNN/train_gnn.py
    python3 GNN/train_gnn.py --hidden 64 --layers 3 --epochs 600 --onnx
"""

import argparse
import glob
import json
import os
import random

import torch
import torch.nn as nn
import torch.nn.functional as F

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA_DIR = os.path.join(ROOT, "GNN", "data")
REPORT_DIR = os.path.join(ROOT, "GNN", "report")
WEIGHTS_PATH = os.path.join(ROOT, "Assets", "Resources", "GNN", "gnn_weights.json")
CLASS_NAMES = ["Safe", "Caution", "Danger"]


# ----------------------------------------------------------------------------- veri
class Graph:
    def __init__(self, path):
        with open(path, encoding="utf-8") as f:
            d = json.load(f)
        n, nf = d["numNodes"], d["numFeatures"]
        self.name = os.path.basename(path)
        self.feature_names = d["featureNames"]
        self.x = torch.tensor(d["features"], dtype=torch.float32).view(n, nf)
        self.y = torch.tensor(d["labels"], dtype=torch.long)
        self.pos = torch.tensor(d["positions"], dtype=torch.float32).view(n, 3)
        self.world_scale = d["worldScale"]
        src = torch.tensor(d["edgeSrc"], dtype=torch.long)
        dst = torch.tensor(d["edgeDst"], dtype=torch.long)
        self.adj = mean_adjacency(src, dst, n)
        # near_labels[i, c] = düğüm i veya 1-komşularından birinin etiketi c mi (toleranslı metrik için)
        onehot = F.one_hot(self.y, 3).float()
        self.near_labels = (onehot + torch.sparse.mm(self.adj, onehot)) > 0
        self.num_nodes = n
        self.num_edges = len(src)


def mean_adjacency(src, dst, n):
    """Satır-normalize komşuluk: (A h)_i = mean_{j in N(i)} h_j. Komşusuz satır sıfır."""
    deg = torch.zeros(n).scatter_add_(0, src, torch.ones(len(src)))
    values = 1.0 / deg[src].clamp(min=1)
    return torch.sparse_coo_tensor(torch.stack([src, dst]), values, (n, n), check_invariants=True).coalesce()


def assign_folds(graphs, block_size, seed, num_folds):
    """
    Uzamsal, katmanlı (stratified) blok bazlı k-katlama. Tüm graflarda aynı blok ataması kullanılır.
    Her blok içerdiği en nadir sınıfa göre bir katmana konur (Danger > Caution > Safe) ve
    her katman katlara sırayla dağıtılır; böylece az sayıdaki tehlikeli bölge her katta temsil edilir.
    """
    block_counts = {}
    graph_keys = []
    for g in graphs:
        k = torch.floor(g.pos[:, [0, 2]] / block_size).long()
        k = [tuple(v) for v in k.tolist()]
        graph_keys.append(k)
        for key, label in zip(k, g.y.tolist()):
            block_counts.setdefault(key, [0, 0, 0])[label] += 1

    strata = {2: [], 1: [], 0: []}
    for key in sorted(block_counts):
        c = block_counts[key]
        strata[2 if c[2] else (1 if c[1] else 0)].append(key)

    rng = random.Random(seed)
    fold_of = {}
    for keys in strata.values():
        rng.shuffle(keys)
        for i, key in enumerate(keys):
            fold_of[key] = i % num_folds

    for g, k in zip(graphs, graph_keys):
        g.fold = torch.tensor([fold_of[v] for v in k])


def set_masks(graphs, test_fold, val_fold):
    """test_fold=None: test kümesi yok (nihai model, performans çapraz doğrulamadan raporlanır)."""
    for g in graphs:
        g.test_mask = g.fold == test_fold if test_fold is not None else torch.zeros_like(g.fold, dtype=torch.bool)
        g.val_mask = g.fold == val_fold
        g.train_mask = ~(g.test_mask | g.val_mask)


# ----------------------------------------------------------------------------- model
class SageLayer(nn.Module):
    def __init__(self, in_dim, out_dim):
        super().__init__()
        self.self_lin = nn.Linear(in_dim, out_dim, bias=True)
        self.neigh_lin = nn.Linear(in_dim, out_dim, bias=False)

    def forward(self, h, adj):
        return F.relu(self.self_lin(h) + self.neigh_lin(torch.sparse.mm(adj, h)))


class GraphSAGE(nn.Module):
    def __init__(self, in_dim, hidden, num_layers, num_classes, dropout):
        super().__init__()
        dims = [in_dim] + [hidden] * num_layers
        self.layers = nn.ModuleList(SageLayer(a, b) for a, b in zip(dims[:-1], dims[1:]))
        self.head = nn.Linear(hidden, num_classes)
        self.dropout = dropout

    def forward(self, x, adj):
        h = x
        for layer in self.layers:
            h = F.dropout(h, self.dropout, self.training)
            h = layer(h, adj)
        return self.head(h)


# ----------------------------------------------------------------------------- metrikler
def evaluate(model, graphs, mask_name, mean, std):
    """
    Katı metrikler + sınır toleranslı metrikler. Etiketler elle çizilmiş kutulardan geldiği için
    tahmin, düğümün kendisinin veya 1-komşusunun etiketiyle uyuşuyorsa toleranslı olarak doğru sayılır.
    """
    model.eval()
    conf = torch.zeros(3, 3, dtype=torch.long)
    tol_conf = torch.zeros(3, 3, dtype=torch.long)
    with torch.no_grad():
        for g in graphs:
            mask = getattr(g, mask_name)
            pred = model((g.x - mean) / std, g.adj).argmax(1)
            for t, p, ok in zip(g.y[mask].tolist(), pred[mask].tolist(), g.near_labels[mask, pred[mask]].tolist()):
                conf[t, p] += 1
                tol_conf[p if ok else t, p] += 1
    result = metrics_from_confusion(conf)
    tolerant = metrics_from_confusion(tol_conf)
    result["tolerant"] = {"accuracy": tolerant["accuracy"], "macro_f1": tolerant["macro_f1"],
                          "per_class_f1": {k: v["f1"] for k, v in tolerant["per_class"].items()}}
    return result


def metrics_from_confusion(conf):
    per_class = {}
    f1s = []
    for c, name in enumerate(CLASS_NAMES):
        tp = conf[c, c].item()
        support = conf[c, :].sum().item()
        prec = tp / max(conf[:, c].sum().item(), 1)
        rec = tp / max(support, 1)
        f1 = 2 * prec * rec / max(prec + rec, 1e-9)
        per_class[name] = {"precision": round(prec, 4), "recall": round(rec, 4), "f1": round(f1, 4),
                           "support": support}
        if support > 0:  # kümede bulunmayan sınıf macro-F1'i yapay olarak düşürmesin
            f1s.append(f1)
    total = conf.sum().item()
    return {
        "accuracy": round(conf.trace().item() / max(total, 1), 4),
        "macro_f1": round(sum(f1s) / max(len(f1s), 1), 4),
        "per_class": per_class,
        "confusion_matrix": conf.tolist(),  # satır = gerçek, sütun = tahmin
    }


# ----------------------------------------------------------------------------- dışa aktarım
def flat(t):
    return [round(v, 7) for v in t.detach().reshape(-1).tolist()]


def export_weights(model, mean, std, feature_names, args, metrics):
    layers = []
    for layer in model.layers:
        w_self = layer.self_lin.weight
        layers.append({
            "inDim": w_self.shape[1], "outDim": w_self.shape[0],
            "selfWeight": flat(w_self), "neighWeight": flat(layer.neigh_lin.weight),
            "bias": flat(layer.self_lin.bias),
        })
    out = {
        "version": 1,
        "architecture": f"GraphSAGE-mean L={args.layers} H={args.hidden}",
        "inputDim": len(feature_names),
        "numClasses": len(CLASS_NAMES),
        "featureNames": feature_names,
        "classNames": CLASS_NAMES,
        "featureMean": flat(mean),
        "featureStd": flat(std),
        "layers": layers,
        "head": {"inDim": model.head.in_features, "outDim": model.head.out_features,
                 "weight": flat(model.head.weight), "bias": flat(model.head.bias)},
        "testMetrics": {"accuracy": metrics["accuracy"], "macro_f1": metrics["macro_f1"]},
    }
    os.makedirs(os.path.dirname(WEIGHTS_PATH), exist_ok=True)
    with open(WEIGHTS_PATH, "w", encoding="utf-8") as f:
        json.dump(out, f)


def export_onnx(model, graph, mean, std):
    """İsteğe bağlı: modeli yoğun komşuluk matrisiyle ONNX olarak kaydeder (rapor / Netron görselleştirme)."""

    class Dense(nn.Module):
        def __init__(self, m):
            super().__init__()
            self.m = m

        def forward(self, x, adj):
            h = (x - mean) / std
            for layer in self.m.layers:
                h = F.relu(layer.self_lin(h) + layer.neigh_lin(adj @ h))
            return torch.softmax(self.m.head(h), dim=1)

    path = os.path.join(REPORT_DIR, "gnn_model.onnx")
    model.eval()
    torch.onnx.export(Dense(model), (graph.x, graph.adj.to_dense()), path,
                      input_names=["node_features", "mean_adjacency"], output_names=["class_probabilities"],
                      dynamic_axes={"node_features": {0: "N"}, "mean_adjacency": {0: "N", 1: "N"},
                                    "class_probabilities": {0: "N"}})
    print("ONNX:", path)


def train_model(graphs, feature_names, args, verbose):
    torch.manual_seed(args.seed)

    # Standardizasyon yalnızca eğitim düğümlerinden. Özellikler [0,1] aralığında; neredeyse sabit
    # bir özelliğin std'si ~0 olursa standardizasyon nadir değerleri patlatır, bu yüzden alt sınır konur.
    train_x = torch.cat([g.x[g.train_mask] for g in graphs])
    mean, std = train_x.mean(0), train_x.std(0).clamp(min=0.05)

    # Sınıf dengesizliği için (karekök) ters frekans ağırlıkları
    train_y = torch.cat([g.y[g.train_mask] for g in graphs])
    counts = torch.bincount(train_y, minlength=3).float().clamp(min=1)
    class_weights = counts.sum() / (3 * counts)
    if args.class_weight == "sqrt":
        class_weights = class_weights.sqrt()
    elif args.class_weight == "none":
        class_weights = torch.ones(3)
    if verbose:
        print("Sınıf ağırlıkları:", [round(w, 2) for w in class_weights.tolist()])

    model = GraphSAGE(len(feature_names), args.hidden, args.layers, 3, args.dropout)
    opt = torch.optim.Adam(model.parameters(), lr=args.lr, weight_decay=args.weight_decay)

    best_f1, best_state, bad_epochs = -1.0, None, 0
    for epoch in range(1, args.epochs + 1):
        model.train()
        opt.zero_grad()
        loss = 0.0
        for g in graphs:
            logits = model((g.x - mean) / std, g.adj)
            loss = loss + F.cross_entropy(logits[g.train_mask], g.y[g.train_mask], weight=class_weights)
        loss = loss / len(graphs)
        loss.backward()
        opt.step()

        val = evaluate(model, graphs, "val_mask", mean, std)
        if val["macro_f1"] > best_f1:
            best_f1, bad_epochs = val["macro_f1"], 0
            best_state = {k: v.clone() for k, v in model.state_dict().items()}
        else:
            bad_epochs += 1

        if verbose and (epoch % 25 == 0 or epoch == 1):
            print(f"epoch {epoch:4d}  loss {loss.item():.4f}  val acc {val['accuracy']:.3f}  val macro-F1 {val['macro_f1']:.3f}")
        if bad_epochs >= args.patience:
            if verbose:
                print(f"Erken durdurma: epoch {epoch}")
            break

    model.load_state_dict(best_state)
    return model, mean, std


def summarize_cv(results):
    def stats(values):
        m = sum(values) / len(values)
        sd = (sum((v - m) ** 2 for v in values) / max(len(values) - 1, 1)) ** 0.5
        return {"mean": round(m, 4), "std": round(sd, 4)}

    summary = {
        "accuracy": stats([r["accuracy"] for r in results]),
        "macro_f1": stats([r["macro_f1"] for r in results]),
        "tolerant_accuracy": stats([r["tolerant"]["accuracy"] for r in results]),
        "tolerant_macro_f1": stats([r["tolerant"]["macro_f1"] for r in results]),
    }
    for name in CLASS_NAMES:
        summary[f"f1_{name}"] = stats([r["per_class"][name]["f1"] for r in results])
        summary[f"tolerant_f1_{name}"] = stats([r["tolerant"]["per_class_f1"][name] for r in results])
    return summary


def print_cv_summary(s):
    f = lambda k: f"{s[k]['mean']:.3f} ± {s[k]['std']:.3f}"
    print(f"\nOrtalama (± std): accuracy {f('accuracy')}  macro-F1 {f('macro_f1')}")
    print(f"  F1  Safe {f('f1_Safe')}  Caution {f('f1_Caution')}  Danger {f('f1_Danger')}")
    print(f"  Toleranslı: accuracy {f('tolerant_accuracy')}  macro-F1 {f('tolerant_macro_f1')}  "
          f"Safe {f('tolerant_f1_Safe')}  Danger {f('tolerant_f1_Danger')}")


# ----------------------------------------------------------------------------- eğitim
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--hidden", type=int, default=32)
    ap.add_argument("--layers", type=int, default=2)
    ap.add_argument("--dropout", type=float, default=0.2)
    ap.add_argument("--lr", type=float, default=0.01)
    ap.add_argument("--weight-decay", type=float, default=5e-4)
    ap.add_argument("--epochs", type=int, default=500)
    ap.add_argument("--patience", type=int, default=80)
    ap.add_argument("--block-size", type=float, default=2.0, help="Uzamsal blok boyu (metre)")
    ap.add_argument("--seed", type=int, default=42)
    ap.add_argument("--class-weight", choices=["inverse", "sqrt", "none"], default="sqrt",
                    help="Sınıf dengesizliği: ters frekans, karekök ters frekans veya yok")
    ap.add_argument("--folds", type=int, default=5)
    ap.add_argument("--repeats", type=int, default=3, help="Çapraz doğrulama tekrar sayısı (farklı karıştırma)")
    ap.add_argument("--no-cv", dest="cv", action="store_false", help="Çapraz doğrulamayı atla")
    ap.add_argument("--onnx", action="store_true")
    args = ap.parse_args()

    paths = sorted(glob.glob(os.path.join(DATA_DIR, "*_graph_*.json")))
    if not paths:
        raise SystemExit(f"Veri yok: {DATA_DIR}. Unity'de Tools > GNN > Eğitim Verisini ve Grafı Dışa Aktar çalıştırın.")

    graphs = [Graph(p) for p in paths]
    feature_names = graphs[0].feature_names
    for g in graphs:
        print(f"{g.name}: {g.num_nodes} düğüm, {g.num_edges // 2} kenar, "
              f"sınıf dağılımı {torch.bincount(g.y, minlength=3).tolist()}")

    block_size = args.block_size * graphs[0].world_scale

    # 1) Tekrarlı k-katlı uzamsal çapraz doğrulama (raporlama için). Her tekrar blokları farklı
    #    karıştırır; küçük veri setinde tek bir bölmenin şansa bağlı oynaklığını azaltır.
    cv_results = []
    if args.cv:
        print(f"\n=== {args.repeats} x {args.folds}-katlı uzamsal çapraz doğrulama ===")
        for r in range(args.repeats):
            assign_folds(graphs, block_size, args.seed + r, args.folds)
            for k in range(args.folds):
                set_masks(graphs, test_fold=k, val_fold=(k + 1) % args.folds)
                model, mean, std = train_model(graphs, feature_names, args, verbose=False)
                res = evaluate(model, graphs, "test_mask", mean, std)
                res["repeat"], res["fold"] = r, k
                cv_results.append(res)
                pc = res["per_class"]
                print(f"tekrar {r} kat {k}: acc {res['accuracy']:.3f}  macro-F1 {res['macro_f1']:.3f}  "
                      f"F1 S/C/D {pc['Safe']['f1']:.2f}/{pc['Caution']['f1']:.2f}/{pc['Danger']['f1']:.2f}  "
                      f"| toleranslı macro-F1 {res['tolerant']['macro_f1']:.3f}")
        cv_summary = summarize_cv(cv_results)
        print_cv_summary(cv_summary)

    assign_folds(graphs, block_size, args.seed, args.folds)

    # 2) Nihai (oyuna giden) model: kat 0 yalnızca erken durdurma için, geri kalan her şey eğitim.
    #    Genelleme performansı yukarıdaki çapraz doğrulamadan raporlanır. --no-cv ile kat 0 test,
    #    kat 1 doğrulama olarak klasik tek bölme kullanılır.
    print("\n=== Nihai model ===")
    if args.cv:
        set_masks(graphs, test_fold=None, val_fold=0)
    else:
        set_masks(graphs, test_fold=0, val_fold=1)
    model, mean, std = train_model(graphs, feature_names, args, verbose=True)
    report = {
        "args": vars(args),
        "graphs": [{"file": g.name, "nodes": g.num_nodes, "edges": g.num_edges // 2} for g in graphs],
        "feature_names": feature_names,
        "cross_validation": {"folds": cv_results, "summary": cv_summary} if args.cv else None,
        "train": evaluate(model, graphs, "train_mask", mean, std),
        "val": evaluate(model, graphs, "val_mask", mean, std),
        "test": evaluate(model, graphs, "test_mask", mean, std) if not args.cv else None,
    }

    os.makedirs(REPORT_DIR, exist_ok=True)
    with open(os.path.join(REPORT_DIR, "metrics.json"), "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2, ensure_ascii=False)

    headline = report["test"] if not args.cv else {
        "accuracy": cv_summary["accuracy"]["mean"], "macro_f1": cv_summary["macro_f1"]["mean"]}
    export_weights(model, mean, std, feature_names, args, headline)
    if args.onnx:
        export_onnx(model, graphs[0], mean, std)

    if not args.cv:
        t = report["test"]
        print("\n=== TEST ===")
        print(f"accuracy {t['accuracy']:.3f}  macro-F1 {t['macro_f1']:.3f}")
        for name, m in t["per_class"].items():
            print(f"  {name:8s} P {m['precision']:.3f}  R {m['recall']:.3f}  F1 {m['f1']:.3f}  (n={m['support']})")
        print("Karışıklık matrisi (satır=gerçek, sütun=tahmin):", t["confusion_matrix"])
        print(f"Toleranslı: accuracy {t['tolerant']['accuracy']:.3f}  macro-F1 {t['tolerant']['macro_f1']:.3f}")
    else:
        v = report["val"]
        print(f"Doğrulama (kat 0): acc {v['accuracy']:.3f}  macro-F1 {v['macro_f1']:.3f}  "
              f"F1 S/C/D {v['per_class']['Safe']['f1']:.2f}/{v['per_class']['Caution']['f1']:.2f}/{v['per_class']['Danger']['f1']:.2f}")
        print("Genelleme performansı için yukarıdaki çapraz doğrulama ortalamasını raporlayın.")
    print("\nAğırlıklar:", WEIGHTS_PATH)


if __name__ == "__main__":
    main()
