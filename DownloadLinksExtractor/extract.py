"""
DownloadLinksExtractor - 从 DownloadSelected.json 中的文章页面提取下载链接、密码和二维码

使用 Playwright + OpenCV 实现：
1. 打开文章详情页（不等待完整加载，只等工具栏出现）
2. 点击工具栏"下载"按钮打开下载页
3. 从下载页提取下载链接、提取码、解压密码
4. 扫描下载页中的二维码图片，解码出网盘地址
"""

import asyncio
import json
import os
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
# 下载按钮选择器（文章底部工具栏：赞/下载/收藏）
DOWNLOAD_BTN_SELECTOR = (
    ".inn-singular__post__toolbar__item__link:has(.fa-cloud-download-alt)"
)
# 下载页内容区选择器
DOWNLOAD_PAGE_CONTENT_SELECTOR = "#inn-download-page__content"


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
    """导航到文章页面，点击下载按钮打开下载页，提取下载信息。"""
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

    download_page = None
    try:
        # 导航到文章页面（只等 HTML 收到，不等图片等子资源）
        await page.goto(url, wait_until="commit", timeout=30000)

        # 等待工具栏的下载按钮出现（赞/下载/收藏中的"下载"）
        download_btn = None
        try:
            download_btn = await page.wait_for_selector(
                DOWNLOAD_BTN_SELECTOR, timeout=15000
            )
        except Exception:
            await wait_for_challenge(page)
            try:
                download_btn = await page.wait_for_selector(
                    DOWNLOAD_BTN_SELECTOR, timeout=15000
                )
            except Exception:
                pass

        if not download_btn:
            result["errors"].append("未找到下载按钮（赞/下载/收藏工具栏）")
            return result

        result["title"] = await page.title()

        # 点击下载按钮，捕获弹出的新标签页
        try:
            async with context.expect_page(timeout=10000) as new_page_info:
                await download_btn.click()
            download_page = await new_page_info.value
        except Exception:
            # 可能在同一标签页导航了
            if "download" in page.url:
                download_page = page
            else:
                result["errors"].append("点击下载按钮后未打开下载页")
                return result

        # 等待下载页 DOM 就绪
        try:
            await download_page.wait_for_load_state(
                "domcontentloaded", timeout=15000
            )
        except Exception:
            pass

        # 等待下载页内容区渲染（JS 动态生成）
        content_el = None
        try:
            content_el = await download_page.wait_for_selector(
                DOWNLOAD_PAGE_CONTENT_SELECTOR, timeout=15000
            )
        except Exception:
            await wait_for_challenge(download_page)
            try:
                content_el = await download_page.wait_for_selector(
                    DOWNLOAD_PAGE_CONTENT_SELECTOR, timeout=15000
                )
            except Exception:
                pass

        if not content_el:
            result["errors"].append("下载页内容未加载")
            return result

        # 遍历所有下载源（每个 fieldset 是一个下载通道）
        fieldsets = await content_el.query_selector_all("fieldset")
        for fieldset in fieldsets:
            legend = await fieldset.query_selector("legend")
            source_name = (await legend.inner_text()).strip() if legend else ""

            # 提取密码
            pwd_input = await fieldset.query_selector(
                ".inn-download-page__content__item__download-pwd input"
            )
            if pwd_input:
                val = await pwd_input.get_attribute("value")
                if val and not result["extract_code"]:
                    result["extract_code"] = val

            # 解压密码
            extract_input = await fieldset.query_selector(
                ".inn-download-page__content__item__extract-pwd input"
            )
            if extract_input:
                val = await extract_input.get_attribute("value")
                if val and not result["unzip_password"]:
                    result["unzip_password"] = val

            # 下载链接（按钮上的 href）
            btn_links = await fieldset.query_selector_all(
                ".inn-download-page__content__btn a[href]"
            )
            for a in btn_links:
                href = await a.get_attribute("href")
                if href and not href.startswith(("#", "javascript")):
                    text = (await a.inner_text()).strip() or "下载"
                    result["download_links"].append({
                        "url": href,
                        "text": f"{source_name} - {text}",
                    })

            # 下载 URL 输入框
            url_input = await fieldset.query_selector(
                ".inn-download-page__content__item__download-url input"
            )
            if url_input:
                val = await url_input.get_attribute("value")
                if val and val.startswith("http"):
                    result["download_links"].append({
                        "url": val,
                        "text": source_name or "下载 URL",
                    })

            # 扫描 QR 码图片（用 context.request 手动获取图片字节）
            imgs = await fieldset.query_selector_all("img[src]")
            for img_el in imgs:
                src = await img_el.get_attribute("src")
                if not src:
                    continue
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
                            "text": f"二维码解码 - {source_name}",
                            "qr_source_image": src,
                        })
                        print(f"  [QR] {src} -> {qr_data}")
                except Exception as e:
                    result["errors"].append(f"图片处理失败 {src}: {e}")

        # 也在整个下载页内容区扫描可能遗漏的网盘链接
        all_links = await content_el.query_selector_all("a[href]")
        download_keywords = [
            "pan.baidu", "baidu.com", "pikpak", "mega.nz", "drive.google",
            "mediafire", "1drv", "onedrive", "terabox", "aliyundrive",
            "123pan", "lanzoui", "lanzou", "weiyun", "yun.cn",
            "magnet:", "mypikpak",
        ]
        existing_urls = {dl["url"] for dl in result["download_links"]}
        for link in all_links:
            href = await link.get_attribute("href")
            if (
                not href
                or href.startswith(("#", "javascript"))
                or href in existing_urls
            ):
                continue
            href_lower = href.lower()
            if any(kw in href_lower for kw in download_keywords):
                text = (await link.inner_text()).strip()
                result["download_links"].append({
                    "url": href,
                    "text": text or "下载链接",
                })

    except Exception as e:
        result["errors"].append(str(e))
    finally:
        # 关闭下载页标签（如果是新开的标签）
        if download_page and download_page != page:
            try:
                await download_page.close()
            except Exception:
                pass

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
            args=["--blink-settings=imagesEnabled=false"],
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
