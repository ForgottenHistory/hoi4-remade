"""Plain text of a HOI4 wiki page saved as HTML (the wiki blocks automated fetches, so pages are saved by hand,
e.g. into D:\\Stuff\\Coding\\hoi4\\fetched wiki pages). Keeps only the article (the mw-parser-output div):
headings as "## ", table cells separated by " | ", image alt text in brackets (icons like [Plains]).

    python Tools/reference/wiki_text.py PAGE.html [OUT.txt]
"""
import re
import sys
from html.parser import HTMLParser


class Article(HTMLParser):
    def __init__(self):
        super().__init__()
        self.out, self.skip, self.on = [], 0, False

    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if "mw-parser-output" in (a.get("class") or ""):
            self.on = True
        if tag in ("script", "style"):
            self.skip += 1
        if tag in ("p", "div", "tr", "li", "h1", "h2", "h3", "h4", "br", "table"):
            self.out.append("\n")
        if tag in ("td", "th"):
            self.out.append(" | ")
        if tag in ("h2", "h3", "h4"):
            self.out.append("## ")
        if tag == "img" and self.on and a.get("alt"):
            self.out.append(f"[{a['alt']}]")

    def handle_endtag(self, tag):
        if tag in ("script", "style"):
            self.skip -= 1

    def handle_data(self, data):
        if self.on and not self.skip:
            self.out.append(data)


def main():
    p = Article()
    p.feed(open(sys.argv[1], encoding="utf-8", errors="replace").read())
    text = re.sub(r"\n\s*\n+", "\n", re.sub(r"[ \t]+", " ", "".join(p.out)))
    if len(sys.argv) > 2:
        open(sys.argv[2], "w", encoding="utf-8").write(text)
    else:
        sys.stdout.reconfigure(encoding="utf-8")
        print(text)


if __name__ == "__main__":
    main()
