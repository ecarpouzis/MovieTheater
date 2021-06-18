import { Button, Input, Tooltip } from "antd";
import { InfoCircleOutlined, UserOutlined } from "@ant-design/icons";

function Login() {
  return (
    <div id="LoginContainer" style={{ color: "white" }}>
      <span style={{ fontWeight: "bold", fontSize: "18px" }}>LOG IN</span>
      <br />
      <br />

      <Input.Group compact>
        <Input
          placeholder="Username"
          prefix={<UserOutlined className="site-form-item-icon" />}
          style={{ width: "150px" }}
          suffix={
            <Tooltip title="This website purposely requires no password to log in.">
              <InfoCircleOutlined style={{ color: "rgba(0,0,0,.45)" }} />
            </Tooltip>
          }
        />
        <Button type="primary">></Button>
      </Input.Group>
    </div>
  );
}
export default Login;
