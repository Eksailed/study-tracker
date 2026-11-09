(() => {
  const main = document.getElementById("docs-main");
  const origin = location.origin;

  const element = (tag, text, className) => {
    const node = document.createElement(tag);
    if (text !== undefined) node.textContent = text;
    if (className) node.className = className;
    return node;
  };

  const showError = message => {
    const box = document.getElementById("docs-error");
    box.textContent = message;
    box.hidden = false;
  };

  const resolve = (document_, schema) => {
    if (!schema || !schema.$ref) return schema;
    return schema.$ref.replace(/^#\//, "").split("/").reduce((node, key) => node && node[key], document_);
  };

  const describeSchema = (document_, schema) => {
    const value = resolve(document_, schema);
    if (!value) return "";
    if (value.type === "array") return "список: " + describeSchema(document_, value.items);
    const type = Array.isArray(value.type) ? value.type.filter(t => t !== "null").join(" | ") : value.type;
    if (value.properties) return "{ " + Object.entries(value.properties).map(([name, property]) => name + ": " + describeSchema(document_, property)).join(", ") + " }";
    if (value.enum) return value.enum.join(" | ");
    return type || "";
  };

  const bodyExample = (document_, schema) => {
    const value = resolve(document_, schema);
    if (!value || !value.properties) return null;
    const sample = {};
    for (const [name, property] of Object.entries(value.properties)) {
      const item = resolve(document_, property) || {};
      const type = Array.isArray(item.type) ? item.type.find(t => t !== "null") : item.type;
      sample[name] = item.enum ? item.enum[0] : type === "integer" || type === "number" ? 1 : type === "boolean" ? false : "…";
    }
    return JSON.stringify(sample);
  };

  const curl = (document_, path, method, operation) => {
    const parts = ["curl -X " + method.toUpperCase(), '-H "Authorization: Bearer zt_…"'];
    const content = operation.requestBody && operation.requestBody.content && operation.requestBody.content["application/json"];
    if (content) {
      parts.push('-H "Content-Type: application/json"');
      const sample = bodyExample(document_, content.schema);
      if (sample) parts.push("-d '" + sample + "'");
    }
    parts.push(origin + path.replace(/\{(\w+)\}/g, "1"));
    return parts.join(" \\\n  ");
  };

  const table = (headers, rows) => {
    const wrap = element("div", undefined, "docs-table-wrap");
    const grid = element("table", undefined, "docs-table");
    const head = element("tr");
    headers.forEach(name => head.append(element("th", name)));
    grid.append(head);
    rows.forEach(cells => {
      const row = element("tr");
      cells.forEach(cell => row.append(element("td", cell)));
      grid.append(row);
    });
    wrap.append(grid);
    return wrap;
  };

  const scopeFor = method => (method === "get" || method === "head" ? "tasks:read" : "tasks:write");

  const renderOperation = (document_, path, method, operation) => {
    const block = element("section", undefined, "docs-op");
    const title = element("h3");
    title.append(element("span", method.toUpperCase(), "docs-method" + (scopeFor(method) === "tasks:write" ? " write" : "")));
    title.append(element("span", path));
    block.append(title);
    if (operation.summary) block.append(element("p", operation.summary, "docs-meta"));
    const scope = scopeFor(method);
    block.append(element("p", "Область токена: " + scope, "docs-meta"));

    const parameters = (operation.parameters || []).map(parameter => {
      const resolved = resolve(document_, parameter) || parameter;
      return [resolved.name, resolved.in === "path" ? "в адресе" : "в запросе", describeSchema(document_, resolved.schema)];
    });
    if (parameters.length) block.append(table(["Параметр", "Где", "Тип"], parameters));

    const content = operation.requestBody && operation.requestBody.content && operation.requestBody.content["application/json"];
    if (content) block.append(element("p", "Тело запроса (JSON): " + describeSchema(document_, content.schema), "docs-meta"));

    block.append(element("pre", curl(document_, path, method, operation), "docs-code"));

    const responses = Object.entries(operation.responses || {}).map(([code, response]) => {
      const json = response.content && response.content["application/json"];
      return [code, (response.description || "").replace(/`/g, ""), json ? describeSchema(document_, json.schema) : ""];
    });
    block.append(table(["Код", "Когда", "Тело"], responses));
    return block;
  };

  const render = document_ => {
    document.getElementById("docs-title").textContent = document_.info.title || "Документация API";
    document.getElementById("docs-description").textContent = (document_.info.description || "").replace(/`/g, "");
    document.querySelectorAll(".docs-origin").forEach(node => { node.textContent = origin; });
    for (const [path, methods] of Object.entries(document_.paths || {})) {
      for (const [method, operation] of Object.entries(methods)) {
        if (["get", "post", "put", "delete", "patch"].includes(method)) main.insertBefore(renderOperation(document_, path, method, operation), document.getElementById("docs-error"));
      }
    }
  };

  fetch("/api/v1/openapi.json", { headers: { Accept: "application/json" } })
    .then(response => {
      if (!response.ok) throw new Error("Сервер ответил " + response.status);
      return response.json();
    })
    .then(render)
    .catch(error => showError("Не удалось загрузить описание API: " + error.message));
})();
