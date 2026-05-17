import json, sys
A = r"D:\CapCut\User Data\Projects\com.lveditor.draft\0512 (2) (VideoEmpty 2026-05-17 09-17-55)\draft_content.json"
B = r"D:\CapCut\User Data\Projects\com.lveditor.draft\0512 (2) (VideoEmpty 2026-05-17 09-17-55) - Copy\draft_content.json"
a = json.load(open(A, encoding="utf-8"))
b = json.load(open(B, encoding="utf-8"))
print("canvas A:", a["canvas_config"]) 
print("canvas B:", b["canvas_config"])
print("texts: A=%d  B=%d" % (len(a["materials"]["texts"]), len(b["materials"]["texts"])))
print("shapes: A=%d  B=%d" % (len(a["materials"]["shapes"]), len(b["materials"]["shapes"])))
# locate target texts
needles = ["1.", "Iteration 9", "Description should not show HTML tags", "Check how to add menu item"]
def find_texts(doc, needle):
    out=[]
    for t in doc["materials"]["texts"]:
        c = t.get("content","")
        if needle in c:
            out.append(t)
    return out
for n in needles:
    print(f"\n=== needle: {n!r}")
    aa = find_texts(a, n); bb = find_texts(b, n)
    print(f"  A matches: {len(aa)}; B matches: {len(bb)}")
