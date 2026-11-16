from PIL import Image, ImageDraw, ImageFont
import math, os, sys, zipfile
S=128; W=(255,255,255,255); LW=17
F=ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", 100)
BLUE=(37,99,235); AMBER=(217,119,6); GREEN=(22,163,74); TEAL=(13,148,136); RED=(220,38,38); ORANGE=(234,88,12); GREY=(107,114,128)
def base(col):
    im=Image.new("RGBA",(S,S),(0,0,0,0)); d=ImageDraw.Draw(im); d.ellipse((2,2,S-3,S-3),fill=col+(255,)); return im,d
def text(d,t,dy=0):
    b=d.textbbox((0,0),t,font=F); w=b[2]-b[0]; h=b[3]-b[1]
    d.text(((S-w)/2-b[0],(S-h)/2-b[1]+dy),t,font=F,fill=W)
def line(d,pts,w=LW): d.line(pts,fill=W,width=w,joint="curve"); [d.ellipse((x-w/2,y-w/2,x+w/2,y+w/2),fill=W) for x,y in (pts[0],pts[-1])]
def check(d): line(d,[(34,66),(55,88),(95,42)],19)
def plus(d): line(d,[(64,32),(64,96)],19); line(d,[(32,64),(96,64)],19)
def cross(d): line(d,[(40,40),(88,88)],19); line(d,[(88,40),(40,88)],19)
def minus(d): line(d,[(34,64),(94,64)],20)
def clock(d): line(d,[(64,64),(64,32)],15); line(d,[(64,64),(88,76)],15)
def play(d): d.polygon([(50,36),(50,92),(94,64)],fill=W)
def envelope(d):
    d.rectangle((28,40,100,90),fill=W); d.line([(28,42),(64,70),(100,42)],fill=d._col,width=9)
def printer(d):
    d.rectangle((42,28,86,48),fill=W)            # Papier oben
    d.rounded_rectangle((26,48,102,84),radius=8,fill=W)  # Gehäuse
    d.rectangle((42,76,86,100),fill=W); d.rectangle((48,82,80,94),fill=d._col)  # Ausgabe
def refresh(d):
    import math
    r=30; pts=[(64+r*math.cos(math.radians(t)),64+r*math.sin(math.radians(t))) for t in range(-60,181,5)]
    line(d,pts,15)
    a=math.radians(180); cx,cy=64+r*math.cos(a),64+r*math.sin(a)
    tx,ty=-math.sin(a),math.cos(a); nx,ny=-ty,tx
    d.polygon([(cx+tx*20,cy+ty*20),(cx+nx*20,cy+ny*20),(cx-nx*20,cy-ny*20)],fill=W)
def box(d):
    d.rectangle((30,40,98,56),fill=W); d.rectangle((36,56,92,94),fill=W); d.rectangle((54,64,74,72),fill=d._col)
SPEC=[("001",BLUE,plus),("010",AMBER,clock),("020",BLUE,play),("030",BLUE,envelope),("100",GREEN,check),("110",GREEN,envelope),
 ("120",GREEN,refresh),("140",GREEN,printer),("200",TEAL,box),("800",RED,lambda d:text(d,"!")),("810",RED,cross),("820",RED,envelope),
 ("830",ORANGE,lambda d:text(d,"?")),("840",RED,printer),("900",GREY,minus)]
out=sys.argv[1]; os.makedirs(out,exist_ok=True); files=[]
for i,(code,col,g) in enumerate(SPEC):
    im,d=base(col); d._col=col+(255,); g(d)
    n=950+i; fn=f"bw{n:06d}.png"; im.resize((16,16),Image.LANCZOS).save(os.path.join(out,fn)); files.append((fn,code))
    im.resize((64,64),Image.LANCZOS).save(f"/tmp/icons/big_{code}.png")
with zipfile.ZipFile(os.path.join(out,"statusbitmaps.zip"),"w") as z:
    for fn,_ in files: z.write(os.path.join(out,fn),fn)
print(files)
# Übersicht
sheet=Image.new("RGBA",(15*80,100),(255,255,255,255))
for i,(fn,code) in enumerate(files):
    sheet.paste(Image.open(f"/tmp/icons/big_{code}.png"),(i*80+8,4),Image.open(f"/tmp/icons/big_{code}.png"))
    s16=Image.open(os.path.join(out,fn)).resize((32,32),Image.NEAREST); sheet.paste(s16,(i*80+24,68),s16)
sheet.save("/tmp/icons/sheet.png")
