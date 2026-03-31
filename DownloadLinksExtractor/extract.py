"""
DownloadLinksExtractor - 从 DownloadSelected.json 中的文章页面提取下载链接、密码和二维码

使用 Playwright + OpenCV 实现：
1. 打开文章详情页（不等待完整加载，只等内容区出现）
2. 点击"下载"按钮展开隐藏内容
3. 提取下载链接、提取码、解压密码
4. 下载图片并尝试扫描二维码，解码出网盘地址
"""

import asyncio
import json
import os
import re
import sys

import cv2
import numpy as np
from playwright.async_api import async_playwright

# ====== 配置 ======
DATA_DIR = r"D:\HMoeWebCrawler"
BASE_URL = "https://www.mhh1.com"
USER_AGENT = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
    "(KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36 Edg/143.0.0.0"
)
# 内容区选择器（按优先级）
CONTENT_SELECTORS = ".entry-content, .post-content, article .content, .article-content"


def scan_qr(image_bytes: bytes) -> str | None:
    """尝试从图片字节中解码二维码，返回解码后的文本或 None。"""
    nparr = np.frombuffer(image_bytes, np.uint8)
    img = cv2.imdecode(nparr, cv2.IMREAD_COLOR)
    if img is None:
        return None
    detector = cv2.QRCodeDetector()
    data, points, _ = detector.detectAndDecode(img)
    if data:
        return data
    try:
        wechat = cv2.wechat_qrcode.WeChatQRCode()
        results, _ = wechat.detectAndDecode(img)
        if results:
            return results[0]
    except (AttributeError, cv2.error):
        pass
    return None


async def wait_for_challenge(page):
    """处理 5s 挑战，只在首页调用一次。"""
    # 等待 DOM 加载即可，不等 networkidle
    try:
        await page.wait_for_load_state("domcontentloaded", timeout=15000)
    except Exception:
        pass

    title = await page.title()
    if any(kw in title.lower() for kw in ["moment", "check", "challenge", "just"]):
        print("  检测到挑战页面，等待自动跳转...")
        try:
            await page.wait_for_url(f"{BASE_URL}/**", timeout=30000)
            await page.wait_for_load_state("domcontentloaded", timeout=15000)
        except Exception:
            pass


async def extract_from_page(page, context, url: str) -> dict:
    """导航到文章页面并提取下载信息。"""
    result = {
        "url": url,
        "title": "",
        "download_links": [],
        "extract_code": None,
        "unzip_password": None,
        "qr_images": [],
        "qr_decoded_urls": [],
        "errors": [],
    }

    try:
        # 只等 commit（HTML 收到即可），不等所有子资源
        await page.goto(url, wait_until="commit", timeout=30000)

        # 等待内容区出现（这是关键——不等 networkidle）
        content_el = None
        try:
            content_el = await page.wait_for_selector(
                CONTENT_SELECTORS, timeout=15000
            )
        except Exception:
            # 可能是挑战页
            await wait_for_challenge(page)
            try:
                content_el = await page.wait_for_selector(
                    CONTENT_SELECTORS, timeout=15000
                )
            except Exception:
                pass

        if not content_el:
            # 退而求其次
            content_el = await page.query_selector("article, .post, main")
        if not content_el:
            result["errors"].append("未找到文章内容区域")
            return result

        result["title"] = await page.title()

        # 1. 展开所有 su-spoiler（包括"下载"按钮）
        spoiler_titles = await page.query_selector_all(".su-spoiler-title")
        for title_el in spoiler_titles:
            try:
                await title_el.click()
                await asyncio.sleep(0.3)
            except Exception:
                pass

        # 也尝试点击 <details> 的 summary
        details_summaries = await page.query_selector_all("details > summary")
        for summary in details_summaries:
            try:
                await summary.click()
                await asyncio.sleep(0.3)
            except Exception:
                pass

        # 短暂等待展开动画
        await asyncio.sleep(0.5)

        # 2. 提取全文本用于密码匹配
        full_text = await content_el.inner_text()

        # 提取码
        for pattern in [
            r"提取码[：:\s\-]*([a-zA-Z0-9]{3,8})",
            r"提取码-([a-zA-Z0-9]{3,8})",
        ]:
            m = re.search(pattern, full_text)
            if m:
                result["extract_code"] = m.group(1)
                break

        # 解压/下载密码
        for pattern in [
            r"(?:解压)?密码[：:\s\-]*([^\s，,。]{2,30})",
            r"PASSWORD[：:\s\-]*([^\s，,。]{2,30})",
        ]:
            m = re.search(pattern, full_text, re.IGNORECASE)
            if m:
                result["unzip_password"] = m.group(1)
                break

        # 3. 提取所有下载链接
        links = await content_el.query_selector_all("a[href]")
        download_keywords = [
            "pan.baidu", "baidu.com", "pikpak", "mega.nz", "drive.google",
            "mediafire", "1drv", "onedrive", "terabox", "aliyundrive",
            "123pan", "lanzoui", "lanzou", "weiyun", "yun.cn",
            "magnet:", "mypikpak",
        ]
        for link in links:
            href = await link.get_attribute("href")
            if not href or href.startswith("#") or href.startswith("javascript"):
                continue
            link_text = (await link.inner_text()).strip()
            href_lower = href.lower()
            text_lower = link_text.lower()
            if any(kw in href_lower or kw in text_lower for kw in download_keywords):
                result["download_links"].append({"url": href, "text": link_text})

        # 4. 查找内容区图片，尝试扫描二维码
        images = await content_el.query_selector_all("img[src]")
        for img_el in images:
            src = await img_el.get_attribute("src")
            if not src:
                continue
            # 跳过明显的大预览图
            try:
                w = await img_el.get_attribute("width")
                h = await img_el.get_attribute("height")
                if w and h and int(w) > 500 and int(h) > 500:
                    continue
            except (ValueError, TypeError):
                pass

            try:
                resp = await context.request.get(src, timeout=10000)
                if not resp.ok:
                    continue
                image_bytes = await resp.body()
                if len(image_bytes) > 500_000:
                    continue

                qr_data = scan_qr(image_bytes)
                if qr_data:
                    result["qr_images"].append(src)
                    result["qr_decoded_urls"].append(qr_data)
                    result["download_links"].append({
                        "url": qr_data,
                        "text": "二维码解码",
                        "qr_source_image": src,
                    })
                    print(f"  [QR] {src} -> {qr_data}")
            except Exception as e:
                result["errors"].append(f"图片处理失败 {src}: {e}")

    except Exception as e:
        result["errors"].append(str(e))

    return result


async def main():
    selected_path = os.path.join(DATA_DIR, "DownloadSelected.json")
    if not os.path.exists(selected_path):
        print(f"错误: 未找到 {selected_path}")
        sys.exit(1)

    with open(selected_path, "r", encoding="utf-8") as f:
        urls = json.load(f)
    print(f"共 {len(urls)} 个 URL 待处理\n")

    # 复用 HMoeWebCrawler 的浏览器数据（保持登录态）
    browser_data_dir = os.path.join(DATA_DIR, "browser-data")
    os.makedirs(browser_data_dir, exist_ok=True)

    results = []

    async with async_playwright() as p:
        context = await p.chromium.launch_persistent_context(
            browser_data_dir,
            headless=False,
            channel="msedge",
            viewport={"width": 1280, "height": 800},
            user_agent=USER_AGENT,
        )

        page = context.pages[0] if context.pages else await context.new_page()
        await page.add_init_script(
            "Object.defineProperty(navigator, 'webdriver', {get: () => undefined})"
        )

        # 先打开首页处理 5s 挑战
        print("正在打开网站...")
        await page.goto(BASE_URL, wait_until="domcontentloaded", timeout=60000)
        await wait_for_challenge(page)
        print(f"网站已就绪: {page.url}\n")

        for i, url in enumerate(urls):
            print(f"[{i + 1}/{len(urls)}] {url}")
            result = await extract_from_page(page, context, url)
            results.append(result)

            if result["title"]:
                print(f"  标题: {result['title']}")
            if result["download_links"]:
                for dl in result["download_links"]:
                    src_info = (
                        f" (来源: {dl['qr_source_image']})"
                        if "qr_source_image" in dl
                        else ""
                    )
                    print(f"  链接: {dl['url']}{src_info}")
            if result["extract_code"]:
                print(f"  提取码: {result['extract_code']}")
            if result["unzip_password"]:
                print(f"  密码: {result['unzip_password']}")
            if result["errors"]:
                for err in result["errors"]:
                    print(f"  错误: {err}")
            print()

            await asyncio.sleep(1)

        await context.close()

    output_path = os.path.join(DATA_DIR, "download_results.json")
    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(results, f, ensure_ascii=False, indent=2)

    print(f"\n结果已保存到 {output_path}")


if __name__ == "__main__":
    asyncio.run(main())
