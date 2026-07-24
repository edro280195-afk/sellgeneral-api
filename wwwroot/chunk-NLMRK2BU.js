import{b as y}from"./chunk-XZR4LXSB.js";import{S as p,X as u,jc as b,oa as h}from"./chunk-3FTFVL6I.js";var g="nenis-brand-theme",x="#FF0072",M="#6D28D9",f=class l{bootstrap=u(y);primary=b(()=>this.bootstrap.me()?.brand.brandPrimaryColor||x);accent=b(()=>this.bootstrap.me()?.brand.brandAccentColor||M);name=b(()=>this.bootstrap.me()?.name??"");logoUrl=b(()=>this.bootstrap.me()?.brand.logoUrl??null);bannerUrl=b(()=>this.bootstrap.me()?.brand.bannerUrl??null);constructor(){h(()=>{this.applyTheme(this.primary(),this.accent())})}applyTheme(n,s){if(typeof document>"u")return;let o=s||M,e=this.buildShades(n),t=this.buildShades(o),r=document.getElementById(g);r||(r=document.createElement("style"),r.id=g,document.head.appendChild(r)),r.textContent=`
            :root {
                --brand-primary: ${n};
                --brand-accent: ${o};
                --brand-primary-50: ${e[50]};
                --brand-primary-100: ${e[100]};
                --brand-primary-200: ${e[200]};
                --brand-primary-300: ${e[300]};
                --brand-primary-400: ${e[400]};
                --brand-primary-500: ${e[500]};
                --brand-primary-600: ${e[600]};
                --brand-primary-700: ${e[700]};
                --brand-primary-800: ${e[800]};
                --brand-primary-900: ${e[900]};
                --brand-accent-50: ${t[50]};
                --brand-accent-500: ${t[500]};
                --brand-accent-700: ${t[700]};
            }
        `}buildShades(n){let{h:s,s:o,l:e}=this.hexToHsl(n),t={50:96,100:92,200:84,300:74,400:60,500:Math.max(35,Math.min(55,e)),600:Math.max(28,e-8),700:Math.max(22,e-16),800:Math.max(16,e-24),900:Math.max(10,e-32)},r={};for(let a of Object.keys(t))r[+a]=this.hslToHex(s,o,t[+a]);return r}hexToHsl(n){let s=n.replace("#",""),o=parseInt(s.substring(0,2),16)/255,e=parseInt(s.substring(2,4),16)/255,t=parseInt(s.substring(4,6),16)/255,r=Math.max(o,e,t),a=Math.min(o,e,t),i=0,c=0,d=(r+a)/2;if(r!==a){let m=r-a;switch(c=d>.5?m/(2-r-a):m/(r+a),r){case o:i=(e-t)/m+(e<t?6:0);break;case e:i=(t-o)/m+2;break;case t:i=(o-e)/m+4;break}i/=6}return{h:i*360,s:c*100,l:d*100}}hslToHex(n,s,o){n=(n%360+360)%360,s=Math.max(0,Math.min(100,s))/100,o=Math.max(0,Math.min(100,o))/100;let e=(1-Math.abs(2*o-1))*s,t=e*(1-Math.abs(n/60%2-1)),r=o-e/2,a=0,i=0,c=0;n<60?(a=e,i=t,c=0):n<120?(a=t,i=e,c=0):n<180?(a=0,i=e,c=t):n<240?(a=0,i=t,c=e):n<300?(a=t,i=0,c=e):(a=e,i=0,c=t);let d=m=>{let $=Math.round((m+r)*255);return Math.max(0,Math.min(255,$)).toString(16).padStart(2,"0")};return`#${d(a)}${d(i)}${d(c)}`.toUpperCase()}static \u0275fac=function(s){return new(s||l)};static \u0275prov=p({token:l,factory:l.\u0275fac,providedIn:"root"})};export{f as a};
