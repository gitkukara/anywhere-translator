"""Create a short, clearly labelled usage illustration from the isolated UI fixtures.
Requires Pillow and ffmpeg. No desktop capture, personal settings, or API calls.
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
import subprocess
import io

ROOT = Path(__file__).resolve().parents[1]
SHOTS = ROOT / 'artifacts/ui-v1.0.0'
OUT = ROOT / 'docs/demo'
OUT.mkdir(parents=True, exist_ok=True)
VIDEO = ROOT / 'dist/Anywhere-Translator-demo.mp4'
FONT = 'C:/Windows/Fonts/msyh.ttc'
def font(size): return ImageFont.truetype(FONT, size)
ink, muted, blue = '#263442', '#667988', '#3390EC'
provider = Image.open(SHOTS / 'theme-light-provider-detail.png').convert('RGBA')
translation = Image.open(SHOTS / 'translation-short.png').convert('RGBA')
provider.thumbnail((470, 430), Image.Resampling.LANCZOS)

def pointer(draw, x, y, click=False):
    if click: draw.ellipse((x-13,y-13,x+13,y+13), outline=blue, width=2)
    draw.polygon([(x,y),(x+3,y+24),(x+9,y+17),(x+16,y+19)], fill=ink, outline='white')

process = subprocess.Popen(['ffmpeg','-y','-loglevel','error','-f','image2pipe','-framerate','6','-i','-',
    '-vf','fps=12','-c:v','libx264','-pix_fmt','yuv420p','-movflags','+faststart',str(VIDEO)],stdin=subprocess.PIPE)
try:
    for n in range(90):
        t=n/6
        step=0 if t<3 else 1 if t<6 else 2 if t<8 else 3 if t<12 else 4
        titles=['1  添加翻译服务','2  拖选文字','3  点击翻译按钮','4  阅读译文','5  点击外部，收起浮窗']
        subtitles=['填写 API Key → 刷新模型 → 选择模型 → 测试连接',
            '在支持取词的应用中，选中想翻译的文字', '选区旁会出现按钮，点击即可翻译',
            '可复制、重试；点击钉住后保持显示', '未钉住时自动收起；也可按 Esc']
        canvas=Image.new('RGB',(900,600),'#F2F5F7');d=ImageDraw.Draw(canvas)
        d.text((34,20),'Anywhere Translator',font=font(20),fill=ink)
        d.text((720,24),'使用示意 · v1.0.0',font=font(14),fill=muted)
        d.line((34,59,866,59),fill='#DEE6EC',width=1)
        d.text((34,74),titles[step],font=font(25),fill=ink)
        d.text((34,112),subtitles[step],font=font(17),fill=muted)
        if step==0:
            # A capture of the real provider editor using isolated fixture data.
            canvas.paste(provider,(450,144),provider)
            d.text((50,220),'翻译服务',font=font(27),fill=ink)
            d.text((50,271),'使用自己的 API Key',font=font(19),fill=muted)
            d.text((50,310),'设置会自动保存',font=font(19),fill=muted)
        else:
            d.rounded_rectangle((50,170,850,504),radius=12,fill='white',outline='#DEE6EC')
            d.text((78,188),'阅读中的一段文字',font=font(16),fill=muted)
            text='Let language be no barrier to reading.'
            y=240
            if step in (1,2,3):
                progress=min(1,(t-3)/2) if step==1 else 1
                selected=text[:max(0,int(len(text)*progress))]
                width=d.textlength(selected,font=font(23))
                if width: d.rectangle((77,y-2,80+width,y+33),fill='#B5D8FA')
            d.text((78,y),text,font=font(23),fill=ink)
            d.text((78,300),'Select a sentence and keep reading.',font=font(20),fill=muted)
            if step==1:
                pointer(d,80+width,y+25)
            if step==2:
                d.rounded_rectangle((561,279,597,315),radius=7,fill=blue)
                d.text((568,282),'翻',font=font(22),fill='white')
                pointer(d,580,300,click=t>7)
            if step==3:
                canvas.paste(translation,(385,310),translation)
            if step==4:
                pointer(d,824,524,click=t<13)
                d.text((78,371),'继续阅读。',font=font(22),fill=ink)
        d.text((34,557),'操作示意，使用模拟译文；非真实 API 调用录像。',font=font(14),fill=muted)
        for i in range(5):
            x=704+i*32
            d.rounded_rectangle((x,564,x+23,568),radius=2,fill=blue if i==step else '#D3DEE6')
        buf=io.BytesIO();canvas.save(buf,format='PNG');process.stdin.write(buf.getvalue())
finally:
    process.stdin.close()
if process.wait()!=0: raise RuntimeError('Video encoding failed')
subprocess.run(['ffmpeg','-y','-loglevel','error','-i',str(VIDEO),'-filter_complex',
    'fps=6,scale=720:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer',
    '-loop','0',str(OUT/'usage.gif')],check=True)
print(VIDEO)
print(OUT/'usage.gif')
