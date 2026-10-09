from pathlib import Path
import subprocess, json, argparse
import numpy as np
from PIL import Image, ImageDraw, ImageFont
import imageio_ffmpeg

parser=argparse.ArgumentParser(description="Upper Tracker fusion / lower raw PICO video comparison")
parser.add_argument("tracker_session", type=Path)
parser.add_argument("pico_session", type=Path)
parser.add_argument("output", type=Path)
parser.add_argument("--hand", choices=["left","right"], default="right")
parser.add_argument("--start", type=float, default=2)
parser.add_argument("--end", type=float, default=23)
parser.add_argument("--font", default="/System/Library/Fonts/Hiragino Sans GB.ttc")
args=parser.parse_args()
if args.start<0 or args.end<=args.start:parser.error("require 0 <= start < end")
folders=[args.tracker_session.resolve(),args.pico_session.resolve()]
out=args.output.resolve();out.parent.mkdir(parents=True,exist_ok=True);outdir=out.parent
joint_start=9 if args.hand=="left" else 191
tracked_column=3 if args.hand=="left" else 6
status_name="fusion_status_left.txt" if args.hand=="left" else "fusion_status.txt"
W,H,FPS=1600,1240,30
colors=['#50DDD0','#FFB36B'];bg='#0D1C27';muted='#A9B8CA'
fonts={n:ImageFont.truetype(args.font,n) for n in [18,21,24,28,32]}
edges=[(0,1)]
for chain in [[1,2,3,4,5],[1,6,7,8,9,10],[1,11,12,13,14,15],[1,16,17,18,19,20],[1,21,22,23,24,25],[6,11,16,21]]:
    edges.extend(zip(chain[:-1],chain[1:]))
streams=[]
for side,folder in enumerate(folders):
    p=np.loadtxt(folder/('poses_fused.txt' if side==0 else 'poses.txt'))
    status=np.loadtxt(folder/'tracking_status.txt');vf=np.loadtxt(folder/'video_frames.txt')
    rows=[l.split() for l in (folder/status_name).read_text().splitlines() if not l.startswith('#')]
    t=p[:,1]/1000;xyz=p[:,joint_start:joint_start+182].reshape(-1,26,7)[:,:,:3]
    valid=status[:,tracked_column]==1
    if side==0:valid=np.array([r[2]=='tracker_root_pico_fingers' for r in rows])
    static=(t>=args.start)&(t<args.end)&valid
    if not np.any(static):raise ValueError(f"No valid static samples in {folder}: {args.start}..{args.end}s")
    wrist=xyz[:,1];reference=np.median(wrist[static],axis=0)
    hand=np.median(xyz[static],axis=0)
    _,v=np.linalg.eigh(np.cov(hand.T));ax=v[:,-1];ay=v[:,-2]
    if np.dot(ax,hand[10]-hand[1])<0:ax=-ax
    if ay[1]<0:ay=-ay
    basis=np.stack([ax,ay],axis=1)
    projection=(xyz-reference)@basis
    q=p[:,joint_start+7+3:joint_start+7+7];q=q/np.maximum(np.linalg.norm(q,axis=1)[:,None],1e-9)
    _,v=np.linalg.eigh(q[static].T@q[static])
    angle=np.degrees(2*np.arccos(np.clip(abs(q@v[:,-1]),0,1)))
    deviation=np.linalg.norm(wrist-reference,axis=1)*100
    streams.append(dict(folder=folder,t=t,vf=vf,xyz=xyz,valid=valid,rows=rows,projection=projection,static=static,reference=reference,basis=basis,angle=angle,deviation=deviation))

# Each session has its own arbitrary world origin. Apply a single constant
# translation and viewing rotation per session, never per-frame recentering.
points=np.concatenate([s['projection'][s['static']].reshape(-1,2) for s in streams])
low,high=np.quantile(points,[.002,.998],axis=0);center=(low+high)/2
scale=min(820/(high[0]-low[0]+.04),280/(high[1]-low[1]+.05))
template=Image.new('RGB',(W,H),bg);d=ImageDraw.Draw(template)
d.text((24,15),'戴 Tracker 与不戴 Tracker：两次录制对照',font=fonts[32],fill='white')
for side,title in enumerate(['录制 A · 戴 Tracker · 融合轨迹','录制 B · 不戴 Tracker · PICO 原始轨迹']):
    offset=side*560;col=colors[side]
    d.text((24,70+offset),title,font=fonts[28],fill=col)
    d.text((24,106+offset),'原始第一视角视频',font=fonts[21],fill=muted)
    x,y=636,160+offset
    d.rounded_rectangle((x,y,x+940,y+370),radius=12,fill='#172A3A')
    for xx in range(x+20,x+935,40):d.line((xx,y+5,xx,y+365),fill='#233D50')
    for yy in range(y+10,y+365,40):d.line((x+5,yy,x+935,yy),fill='#233D50')
    d.text((x+16,y+10),'世界坐标 · 每段固定视角 · 相同米制比例',font=fonts[18],fill=muted)
    d.line((x+22,y+340,x+22+.05*scale,y+340),fill=muted,width=3)
    d.text((x+22,y+305),'5 cm',font=fonts[18],fill=muted)
    s=streams[side]
    p95=np.percentile(s['deviation'][s['static']],95);a95=np.percentile(s['angle'][s['static']],95)
    d.text((636,110+offset),f'静止段 {args.start:g}–{args.end:g} 秒：位置 P95 {p95:.2f} cm  /  朝向 P95 {a95:.2f}°',font=fonts[24],fill='white')
d.line((24,610,1576,610),fill='#405A70',width=2)
d.text((24,1200),'按各自录制经过时间并排播放；头显动作不同。手腕波动衡量稳定性，不代表绝对精度。',font=fonts[21],fill=muted)
ff=imageio_ffmpeg.get_ffmpeg_exe()
decs=[subprocess.Popen([ff,'-v','error','-i',str(s['folder']/'internal.mp4'),'-vf',f'fps={FPS},scale=560:420','-f','rawvideo','-pix_fmt','rgb24','-'],stdout=subprocess.PIPE) for s in streams]
enc=subprocess.Popen([ff,'-y','-v','error','-f','rawvideo','-pix_fmt','rgb24','-s',f'{W}x{H}','-r',str(FPS),'-i','-','-an','-c:v','libx264','-preset','fast','-crf','19','-pix_fmt','yuv420p','-movflags','+faststart',str(out)],stdin=subprocess.PIPE)
frame=0;max_diff=[0.,0.]
while True:
    raws=[dec.stdout.read(560*420*3) for dec in decs]
    if any(not raw for raw in raws):break
    assert all(len(raw)==560*420*3 for raw in raws)
    im=template.copy();d=ImageDraw.Draw(im)
    for side,s in enumerate(streams):
        offset=side*560;col=colors[side]
        im.paste(Image.frombytes('RGB',(560,420),raws[side]),(24,140+offset))
        sec=float(np.interp(frame/FPS,s['vf'][:,2]/1e9,s['vf'][:,1]/1000))
        idx=int(np.argmin(abs(s['t']-sec)));max_diff[side]=max(max_diff[side],abs(s['t'][idx]-sec))
        x,y=636,160+offset;panel=im.crop((x,y,x+940,y+370));pd=ImageDraw.Draw(panel)
        coords=s['projection'][idx]-center;coords=coords*np.array([scale,-scale])+np.array([470,190])
        tail=(s['t']>=sec-1)&(s['t']<=sec)&s['valid']
        trail=(s['projection'][tail,1]-center)*np.array([scale,-scale])+np.array([470,190])
        if len(trail)>1:pd.line([tuple(c) for c in trail],fill='#2E817E' if side==0 else '#966E4C',width=3)
        if s['valid'][idx]:
            for a,b in edges:pd.line([tuple(coords[a]),tuple(coords[b])],fill=col,width=4)
            for j,(u,v) in enumerate(coords):
                rad=7 if j==1 else 4;pd.ellipse((u-rad,v-rad,u+rad,v+rad),fill=col)
        else:pd.text((345,175),'手追不可用',font=fonts[28],fill=col)
        im.paste(panel,(x,y));d=ImageDraw.Draw(im)
        d.text((24,570+offset),f'各自录制时间 {sec:05.2f} 秒',font=fonts[21],fill='white')
        label='已标定 · Tracker 手腕 + PICO 手指' if side==0 else '无 Tracker · PICO 原始手追'
        if side==0 and s['rows'][idx][2]!='tracker_root_pico_fingers':label={'pico_calibrating':'标定中 · 暂用 PICO','pico_only':'配对中 · 暂用 PICO'}.get(s['rows'][idx][2],'手追不可用')
        d.text((636,540+offset),label,font=fonts[24],fill=col)
        if s['valid'][idx] and args.start<=sec<args.end:d.text((636,574+offset),f"当前腕部波动 {s['deviation'][idx]:.2f} cm / {s['angle'][idx]:.2f}°",font=fonts[21],fill=muted)
    if frame in [150,450,780]:im.save(out.with_name(f'{out.stem}_preview_{frame}.jpg'),quality=92)
    enc.stdin.write(im.tobytes());frame+=1
    if frame%240==0:print(f'Rendered {frame} frames',flush=True)
enc.stdin.close();assert enc.wait()==0
for dec in decs:
    if dec.poll() is None:dec.terminate()
    dec.stdout.close();dec.wait()
metadata={'output':str(out),'frames':frame,'fps':FPS,'duration_s':frame/FPS,'bytes':out.stat().st_size,'sources':[str(f) for f in folders],'layout':'Upper yesterday original RGB and actual fused world joints; lower today original RGB and raw world joints','end_policy':'Stop both at end of shorter recording, no frozen padding','alignment':'Each own video encoder PTS mapped to own pose clock; equal elapsed video playback time, not same motion','maximum_nearest_pose_difference_ms':[x*1000 for x in max_diff],'pixels_per_meter':float(scale),'statistics_window_s':[args.start,args.end],'hand':args.hand,'world_view':'Per-session constant viewing rotation and median-wrist translation, shared projection center and scale, no framewise recentering or smoothing','basis':[s['basis'].tolist() for s in streams],'reference':[s['reference'].tolist() for s in streams]}
out.with_suffix('.json').write_text(json.dumps(metadata,ensure_ascii=False,indent=2));print(json.dumps(metadata,ensure_ascii=False),flush=True)
